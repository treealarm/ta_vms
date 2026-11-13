using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain.Arming;

namespace VmsMap.Domain.Control
{
  /// <summary>
  /// One row of the operator's control list: an object the operator can act on — a camera, a
  /// digital input or a relay — with what it currently resolves to.
  ///
  /// A camera's <c>Armed</c> is its own flag in VmsCfg; a sensor's is what the arming scheme
  /// resolves to (and <c>ArmingSource</c> says where from); a relay is never armed, only switched,
  /// so it carries a live <c>State</c> and no arming at all.
  /// </summary>
  public sealed record ControlObjectRow(
    string ObjectId,
    string SourceKind,
    string SourceId,
    string PartKind,
    string PartToken,
    string Name,
    string CameraName,
    string SpaceId,
    string SpaceName,
    string State,
    string ArmMode,
    bool AlarmEnabled,
    bool Configured,
    bool HasArming,
    bool Armed,
    string ArmingSource,
    string ArmingSourceObjectId,
    bool CanControl)
  {
    public const string CameraPart = "device";
    public const string CameraKind = "camera";

    /// <summary>The wire kind: a device node is a camera, anything else is its own part kind.</summary>
    public string Kind => string.Equals(PartKind, CameraPart, StringComparison.Ordinal)
      ? CameraKind
      : PartKind;
  }

  /// <summary>A page of control objects and the position after it, if there may be more.</summary>
  public sealed record ControlObjectPage(
    IReadOnlyList<ControlObjectRow> Rows,
    bool HasMore,
    string? NextName,
    string? NextId);

  /// <summary>
  /// The server-side list behind the operator's Control panel. It exists so that a deployment of
  /// 100 000 cameras never sends 100 000 objects to a browser: the filters run here, the page is
  /// small, and the caller's right is applied before a row leaves the service.
  ///
  /// A row is a <c>device_node</c>, not a settings row: the physical part exists before an
  /// administrator names it, and the panel shows it with its toggle disabled. Parts of a camera are
  /// one device family — the camera itself, its digital inputs and its relays — and the contract
  /// stays source-addressed so another kind of hub (a fire panel) joins it without a second list.
  /// </summary>
  public sealed class ControlObjectQuery
  {
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    // A caller whose rights hide most rows must not make one request walk the whole deployment.
    private const int MaxScanRows = 2000;

    private const string CameraSource = "camera";
    private const string DevicePart = ControlObjectRow.CameraPart;
    private const string DigitalInputPart = "digital_input";
    private const string RelayPart = "relay";
    private const string Inherit = "inherit";

    // The state values a part reports. A part's state set holds one of them.
    private const string SensorOpen = "SENSOR_OPEN";
    private const string SensorClosed = "SENSOR_CLOSED";
    private const string RelayActive = "RELAY_ACTIVE";
    private const string RelayInactive = "RELAY_INACTIVE";

    private static readonly string[] AllParts = [DevicePart, DigitalInputPart, RelayPart];

    private readonly VmsMapDbContext _db;
    private readonly IMapService _map;
    private readonly IRightsService _rights;
    private readonly IDeviceSettingsSource _settings;
    private readonly DeviceObjectSettingsStore _objectSettings;
    private readonly ICameraStateSource _cameraState;
    private readonly EffectiveArmingResolver _arming;

    public ControlObjectQuery(
      VmsMapDbContext db,
      IMapService map,
      IRightsService rights,
      IDeviceSettingsSource settings,
      DeviceObjectSettingsStore objectSettings,
      ICameraStateSource cameraState,
      EffectiveArmingResolver arming)
    {
      _db = db;
      _map = map;
      _rights = rights;
      _settings = settings;
      _objectSettings = objectSettings;
      _cameraState = cameraState;
      _arming = arming;
    }

    /// <summary>
    /// Translates the wire kind to the part kinds it selects. Null means everything; an unknown
    /// kind is the caller's mistake and raises <see cref="ArgumentException"/>.
    /// </summary>
    public static string[]? ParseKind(string? kind)
    {
      if (string.IsNullOrWhiteSpace(kind))
        return null;

      return kind switch
      {
        ControlObjectRow.CameraKind or DevicePart => [DevicePart],
        DigitalInputPart => [DigitalInputPart],
        RelayPart => [RelayPart],
        _ => throw new ArgumentException(
          $"kind must be '{ControlObjectRow.CameraKind}', '{DigitalInputPart}' or '{RelayPart}'",
          nameof(kind)),
      };
    }

    public async Task<ControlObjectPage> ListAsync(
      string? spaceId,
      string? kind,
      string? cameraId,
      int pageSize,
      string? afterName,
      string? afterId,
      CallerContext caller,
      CancellationToken ct)
    {
      var limit = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
      var space = string.IsNullOrWhiteSpace(spaceId) ? null : spaceId;
      var camera = string.IsNullOrWhiteSpace(cameraId) ? null : cameraId;
      var parts = ParseKind(kind) ?? AllParts;

      var rows = new List<ControlObjectRow>(limit);
      string? cursorName = string.IsNullOrWhiteSpace(afterName) ? null : afterName;
      string? cursorId = string.IsNullOrWhiteSpace(afterId) ? null : afterId;

      var exhausted = false;
      var scanned = 0;
      while (rows.Count < limit && !exhausted && scanned < MaxScanRows)
      {
        var chunk = await FetchAsync(space, parts, camera, limit + 1, cursorName, cursorId, ct);
        exhausted = chunk.Count <= limit;
        var page = exhausted ? chunk : chunk.Take(limit).ToList();
        if (page.Count == 0)
        {
          exhausted = true;
          break;
        }

        scanned += page.Count;
        var allowed = await _rights.AllowedAsync(
          page.Select(r => r.ObjectId).ToList(), RightActions.ViewLive, caller, ct);

        // The cursor may only move past rows this page actually considered. When the page fills
        // in the middle of a chunk, the rows behind it are not "hidden", they are "not read yet":
        // moving the cursor past them would drop them from every later page.
        var stoppedAt = -1;
        for (var i = 0; i < page.Count; i++)
        {
          var row = page[i];
          if (!allowed.Contains(row.ObjectId))
            continue;

          rows.Add(row);
          if (rows.Count == limit)
          {
            stoppedAt = i;
            break;
          }
        }

        var lastSeen = stoppedAt >= 0 ? page[stoppedAt] : page[^1];
        cursorName = lastSeen.Name;
        cursorId = lastSeen.ObjectId;

        if (stoppedAt >= 0 && stoppedAt < page.Count - 1)
          exhausted = false;

        if (exhausted || rows.Count == limit)
          break;
      }

      if (rows.Count == 0)
        return new ControlObjectPage([], false, null, null);

      var complete = await CompleteAsync(rows, caller, ct);
      var hasMore = !exhausted || scanned >= MaxScanRows;
      var last = complete[^1];

      return new ControlObjectPage(
        complete,
        hasMore,
        hasMore ? last.Name : null,
        hasMore ? last.ObjectId : null);
    }

    /// <summary>
    /// One object by any of its nodes — the canonical one or a replica of it. A replica resolves
    /// through its owner, because the part is the object and the drawing is not. The answer is the
    /// same row the list would show; a node the caller may not see answers null, exactly as if it
    /// did not exist.
    /// </summary>
    public async Task<ControlObjectRow?> FindAsync(
      string nodeId, CallerContext caller, CancellationToken ct)
    {
      var nodeGuid = Utils.ConvertObjectIdToGuid(nodeId);
      if (nodeGuid is null)
        return null;

      var marker = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == nodeGuid.Value, ct);
      if (marker is null)
        return null;

      var canonicalGuid = marker.owner_id ?? marker.id;
      var device = await _db.DeviceNodes.AsNoTracking()
        .FirstOrDefaultAsync(n => n.object_id == canonicalGuid, ct);
      if (device is null || !string.Equals(device.source_kind, CameraSource, StringComparison.Ordinal))
        return null;

      var canonicalId = Utils.ConvertGuidToObjectId(canonicalGuid);
      var allowed = await _rights.AllowedAsync([canonicalId], RightActions.ViewLive, caller, ct);
      if (!allowed.Contains(canonicalId))
        return null;

      var name = (await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == canonicalGuid, ct))?.name ?? device.part_token;
      var cameraNode = await _db.DeviceNodes.AsNoTracking()
        .FirstOrDefaultAsync(n => n.source_kind == device.source_kind
                                  && n.source_id == device.source_id
                                  && n.part_kind == DevicePart, ct);
      var cameraName = cameraNode is null
        ? string.Empty
        : (await _db.Markers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.id == cameraNode.object_id, ct))?.name ?? string.Empty;

      var reference = await _db.CameraPlacements.AsNoTracking()
        .FirstOrDefaultAsync(p => p.camera_id == device.source_id && p.is_reference, ct);
      var spaceId = reference is null
        ? string.Empty
        : await SpaceOfAsync(reference.object_id, ct);
      var spaceName = string.IsNullOrEmpty(spaceId)
        ? string.Empty
        : (await _db.Markers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.id == Utils.ConvertObjectIdToGuid(spaceId)!.Value, ct))?.name
          ?? string.Empty;

      var state = await LiveStateAsync(canonicalGuid, ct);
      var row = new ControlObjectRow(
        canonicalId,
        device.source_kind,
        device.source_id,
        device.part_kind,
        device.part_token,
        name,
        cameraName,
        spaceId,
        spaceName,
        state,
        string.Empty,
        false,
        false,
        false,
        false,
        string.Empty,
        string.Empty,
        false);

      var complete = await CompleteAsync([row], caller, ct);
      return complete[0];
    }

    /// <summary>
    /// Adds what the page needs from outside the tree: the configured mode and alarm flag, the
    /// effective arming of the sensors, the camera flags, the live state, and whether the caller
    /// may drive an action. All of them are batch reads.
    /// </summary>
    private async Task<List<ControlObjectRow>> CompleteAsync(
      List<ControlObjectRow> rows, CallerContext caller, CancellationToken ct)
    {
      var cameraIds = rows
        .Select(r => r.SourceId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var settings = await _settings.ListManyAsync(cameraIds, ct);
      // The row's own mode and alarm rule come from the map, where a device object's behaviour
      // lives; the settings above only say whether an administrator configured the part at all.
      var objectSettings = await _objectSettings.GetManyAsync(
        rows.Select(r => r.ObjectId).ToList(), ct);

      // Cameras and sensors resolve through one scheme: a camera's own mode, else the zone it
      // stands in. One pass for the page.
      var armables = rows
        .Where(r => string.Equals(r.PartKind, DigitalInputPart, StringComparison.Ordinal)
                    || string.Equals(r.PartKind, DevicePart, StringComparison.Ordinal))
        .Select(r => new ArmingSubject(r.ObjectId, r.SourceId, r.PartKind, r.PartToken))
        .ToList();
      var arming = armables.Count == 0
        ? new Dictionary<string, NodeArmingInfo>(StringComparer.Ordinal)
        : await _arming.ResolveManyAsync(armables, ct);

      var control = await _rights.AllowedAsync(
        rows.Select(r => r.ObjectId).ToList(), RightActions.Control, caller, ct);

      var result = new List<ControlObjectRow>(rows.Count);
      foreach (var row in rows)
      {
        var isCamera = string.Equals(row.PartKind, DevicePart, StringComparison.Ordinal);
        var isSensor = string.Equals(row.PartKind, DigitalInputPart, StringComparison.Ordinal);

        var configured = settings.GetValueOrDefault(row.SourceId)?.FirstOrDefault(s =>
          string.Equals(s.Kind, row.PartKind, StringComparison.Ordinal)
          && string.Equals(s.Token, row.PartToken, StringComparison.Ordinal));
        var own = objectSettings.GetValueOrDefault(row.ObjectId)
          ?? DeviceObjectSettings.Default(row.ObjectId);

        if (isCamera)
        {
          var camera = arming.GetValueOrDefault(row.ObjectId);
          result.Add(row with
          {
            Configured = true,
            HasArming = true,
            ArmMode = camera?.Mode ?? Inherit,
            Armed = camera?.Armed ?? false,
            ArmingSource = camera?.Source ?? EffectiveArmingResolver.None,
            ArmingSourceObjectId = camera?.SourceObjectId ?? string.Empty,
            CanControl = control.Contains(row.ObjectId),
          });
          continue;
        }

        var resolved = isSensor ? arming.GetValueOrDefault(row.ObjectId) : null;
        result.Add(row with
        {
          ArmMode = isSensor ? own.Mode : string.Empty,
          AlarmEnabled = isSensor && own.AlarmEnabled,
          Configured = configured is not null,
          HasArming = isSensor,
          Armed = resolved?.Armed ?? false,
          ArmingSource = resolved?.Source ?? string.Empty,
          ArmingSourceObjectId = resolved?.SourceObjectId ?? string.Empty,
          CanControl = control.Contains(row.ObjectId),
        });
      }

      return result;
    }

    /// <summary>
    /// One keyset page straight from the database, ordered by the object's name. A relational
    /// database walks the space's subtree with one CTE; the tests' in-memory provider gets the
    /// same selection in LINQ, because the provider has no SQL to run.
    /// </summary>
    private async Task<List<ControlObjectRow>> FetchAsync(
      string? spaceId,
      IReadOnlyList<string> parts,
      string? cameraId,
      int limit,
      string? afterName,
      string? afterId,
      CancellationToken ct)
    {
      return _db.Database.IsRelational()
        ? await FetchRelationalAsync(spaceId, parts, cameraId, limit, afterName, afterId, ct)
        : await FetchInMemoryAsync(spaceId, parts, cameraId, limit, afterName, afterId, ct);
    }

    private async Task<List<ControlObjectRow>> FetchRelationalAsync(
      string? spaceId,
      IReadOnlyList<string> parts,
      string? cameraId,
      int limit,
      string? afterName,
      string? afterId,
      CancellationToken ct)
    {
      // The CTE and the parameter exist only when a space is asked for: a bound @space without a
      // value is an error, and an unused parameter is not allowed either.
      var spaceCte = spaceId is null
        ? string.Empty
        : """
          WITH RECURSIVE sub AS (
            SELECT id FROM objects WHERE id = @space
            UNION ALL
            SELECT o.id FROM objects o JOIN sub s ON o.parent_id = s.id
          )
          """;
      var spaceClause = spaceId is null
        ? string.Empty
        : "AND p.object_id IN (SELECT id FROM sub)";
      var cameraClause = cameraId is null ? string.Empty : "AND dn.source_id = @camera";
      var cursorClause = afterName is null
        ? string.Empty
        : "AND (part.name, dn.object_id) > (@after_name, @after_id)";
      var partClause = string.Join(", ", parts.Select(p => $"'{p}'"));

      var sql = $"""
        {spaceCte}
        SELECT dn.object_id,
               dn.source_kind,
               dn.source_id::text,
               dn.part_kind,
               dn.part_token,
               part.name,
               COALESCE(cam.name, ''),
               COALESCE(g.canvas_id::text, ''),
               COALESCE(space.name, ''),
               COALESCE(sv.state, '')
        FROM device_node dn
        JOIN objects part ON part.id = dn.object_id
        LEFT JOIN camera_placement p ON p.camera_id = dn.source_id AND p.is_reference
        LEFT JOIN geo_objects g ON g.id = p.object_id
        LEFT JOIN objects space ON space.id = g.canvas_id
        LEFT JOIN device_node cam_dn
               ON cam_dn.source_kind = dn.source_kind
              AND cam_dn.source_id = dn.source_id
              AND cam_dn.part_kind = '{DevicePart}'
        LEFT JOIN objects cam ON cam.id = cam_dn.object_id
        LEFT JOIN object_state_values sv
               ON sv.object_id = dn.object_id
              AND sv.state IN ('{SensorOpen}', '{SensorClosed}', '{RelayActive}', '{RelayInactive}')
        WHERE dn.source_kind = '{CameraSource}'
          AND dn.part_kind IN ({partClause})
          {spaceClause}
          {cameraClause}
          {cursorClause}
        ORDER BY part.name, dn.object_id
        LIMIT @limit;
        """;

      var result = new List<ControlObjectRow>(limit);
      var connection = _db.Database.GetDbConnection();
      var opened = connection.State != System.Data.ConnectionState.Open;
      if (opened)
        await _db.Database.OpenConnectionAsync(ct);

      try
      {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        if (spaceId is not null)
          cmd.Parameters.Add(new NpgsqlParameter("space", Guid.Parse(spaceId)));
        if (cameraId is not null)
          cmd.Parameters.Add(new NpgsqlParameter("camera", Guid.Parse(cameraId)));
        if (afterName is not null)
        {
          cmd.Parameters.Add(new NpgsqlParameter("after_name", afterName));
          cmd.Parameters.Add(new NpgsqlParameter("after_id", Guid.Parse(afterId!)));
        }
        cmd.Parameters.Add(new NpgsqlParameter("limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
          result.Add(new ControlObjectRow(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            LogicalState(reader.GetString(9)),
            string.Empty,
            false,
            false,
            false,
            false,
            string.Empty,
            string.Empty,
            false));
        }
      }
      finally
      {
        if (opened)
          await _db.Database.CloseConnectionAsync();
      }

      return result;
    }

    private async Task<List<ControlObjectRow>> FetchInMemoryAsync(
      string? spaceId,
      IReadOnlyList<string> parts,
      string? cameraId,
      int limit,
      string? afterName,
      string? afterId,
      CancellationToken ct)
    {
      var subtree = spaceId is null
        ? null
        : await _map.GetSubtreeIdsAsync([spaceId]);

      var nodes = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == CameraSource && parts.Contains(n.part_kind))
        .Where(n => cameraId == null || n.source_id == cameraId)
        .ToListAsync(ct);
      var placements = await _db.CameraPlacements.AsNoTracking()
        .Where(p => p.is_reference)
        .ToDictionaryAsync(p => p.camera_id, StringComparer.Ordinal, ct);
      var canvases = await _db.GeoObjects.AsNoTracking()
        .Where(g => g.canvas_id != null)
        .ToDictionaryAsync(g => g.id, g => g.canvas_id!.Value, ct);
      var markers = await _db.Markers.AsNoTracking()
        .ToDictionaryAsync(m => m.id, ct);
      var cameraNodes = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == CameraSource && n.part_kind == DevicePart)
        .ToDictionaryAsync(n => n.source_id, n => n.object_id, StringComparer.Ordinal);
      var states = await LiveStatesAsync(
        nodes.Select(n => n.object_id).ToList(), ct);

      var rows = new List<ControlObjectRow>();
      foreach (var node in nodes)
      {
        // A camera with no reference placement stands nowhere; the list still has it when no
        // space was asked for, and a space filter leaves it out through the null placement.
        placements.TryGetValue(node.source_id, out var placement);
        if (subtree is not null)
        {
          if (placement is null || !subtree.Contains(placement.object_id.ToString()))
            continue;
        }

        var name = markers.GetValueOrDefault(node.object_id)?.name ?? node.part_token;
        if (afterName is not null && !IsAfter(name, node.object_id.ToString(), afterName, afterId!))
          continue;

        var spaceIdOfRow = placement is null
          ? (Guid?)null
          : canvases.GetValueOrDefault(placement.object_id);
        var cameraName = cameraNodes.TryGetValue(node.source_id, out var cameraNode)
          ? markers.GetValueOrDefault(cameraNode)?.name ?? string.Empty
          : string.Empty;
        var spaceName = spaceIdOfRow is null
          ? string.Empty
          : markers.GetValueOrDefault(spaceIdOfRow.Value)?.name ?? string.Empty;

        rows.Add(new ControlObjectRow(
          node.object_id.ToString(),
          node.source_kind,
          node.source_id,
          node.part_kind,
          node.part_token,
          name,
          cameraName,
          spaceIdOfRow?.ToString() ?? string.Empty,
          spaceName,
          LogicalState(states.GetValueOrDefault(node.object_id) ?? string.Empty),
          string.Empty,
          false,
          false,
          false,
          false,
          string.Empty,
          string.Empty,
          false));
      }

      return rows
        .OrderBy(r => r.Name, StringComparer.Ordinal)
        .ThenBy(r => r.ObjectId, StringComparer.Ordinal)
        .Take(limit)
        .ToList();
    }

    /// <summary>The state a part reports, read once for a page of them.</summary>
    private async Task<Dictionary<Guid, string>> LiveStatesAsync(
      IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
      if (ids.Count == 0)
        return new Dictionary<Guid, string>();

      var wanted = new[] { SensorOpen, SensorClosed, RelayActive, RelayInactive };
      var values = await _db.Set<DBObjectStateValue>().AsNoTracking()
        .Where(v => ids.Contains(v.object_id) && wanted.Contains(v.state))
        .Select(v => new { v.object_id, v.state })
        .ToListAsync(ct);

      return values
        .GroupBy(v => v.object_id)
        .ToDictionary(g => g.Key, g => g.First().state);
    }

    private async Task<string> LiveStateAsync(Guid id, CancellationToken ct)
    {
      var states = await LiveStatesAsync([id], ct);
      return LogicalState(states.GetValueOrDefault(id) ?? string.Empty);
    }

    /// <summary>The nearest canvas above a placed replica: the space its picture stands in.</summary>
    private async Task<string> SpaceOfAsync(Guid replicaId, CancellationToken ct)
    {
      var canvas = await _db.GeoObjects.AsNoTracking()
        .Where(g => g.id == replicaId && g.canvas_id != null)
        .Select(g => g.canvas_id)
        .FirstOrDefaultAsync(ct);
      return canvas?.ToString() ?? string.Empty;
    }

    // The map's state names say what the device reports; the wire says what an operator reads.
    private static string LogicalState(string raw) => raw switch
    {
      SensorOpen => "open",
      SensorClosed => "closed",
      RelayActive => "active",
      RelayInactive => "inactive",
      _ => string.Empty,
    };

    private static bool IsAfter(string name, string id, string afterName, string afterId)
    {
      var byName = string.CompareOrdinal(name, afterName);
      return byName > 0 || (byName == 0 && string.CompareOrdinal(id, afterId) > 0);
    }
  }
}
