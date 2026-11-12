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

namespace VmsMap.Domain.Arming
{
  /// <summary>
  /// One row of the operator's guarding list: a sensor with what it is configured as and what it
  /// currently resolves to.
  /// </summary>
  public sealed record GuardSensorRow(
    string ObjectId,
    string SourceKind,
    string SourceId,
    string PartKind,
    string PartToken,
    string Name,
    string CameraName,
    string SpaceId,
    string SpaceName,
    string ArmMode,
    bool AlarmEnabled,
    bool Configured,
    bool Armed,
    string ArmingSource,
    bool CanControl);

  /// <summary>A page of sensors and the position after it, if there may be more.</summary>
  public sealed record GuardSensorPage(
    IReadOnlyList<GuardSensorRow> Rows,
    bool HasMore,
    string? NextName,
    string? NextId);

  /// <summary>
  /// The server-side list behind the operator's guarding panel. It exists so that a deployment of
  /// 100 000 cameras never sends 100 000 sensors to a browser: the filters run here, the page is
  /// small, and the caller's right is applied before a row leaves the service.
  ///
  /// A sensor is an observed digital input (<c>device_node</c>), not a settings row: the physical
  /// contact exists before an administrator names it, and the panel shows it with its toggle
  /// disabled. Its settings and the camera's armed flag are read in bulk, and the arming of the
  /// whole page is resolved in one pass.
  /// </summary>
  public sealed class GuardSensorQuery
  {
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    // A caller whose rights hide most rows must not make one request walk the whole deployment.
    private const int MaxScanRows = 2000;

    private const string CameraSource = "camera";
    private const string DevicePart = "device";
    private const string DigitalInputPart = "digital_input";
    private const string Inherit = "inherit";

    private readonly VmsMapDbContext _db;
    private readonly IMapService _map;
    private readonly IRightsService _rights;
    private readonly IDeviceSettingsSource _settings;
    private readonly EffectiveArmingResolver _arming;

    public GuardSensorQuery(
      VmsMapDbContext db,
      IMapService map,
      IRightsService rights,
      IDeviceSettingsSource settings,
      EffectiveArmingResolver arming)
    {
      _db = db;
      _map = map;
      _rights = rights;
      _settings = settings;
      _arming = arming;
    }

    public async Task<GuardSensorPage> ListAsync(
      string? spaceId,
      int pageSize,
      string? afterName,
      string? afterId,
      CallerContext caller,
      CancellationToken ct)
    {
      var limit = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
      var space = string.IsNullOrWhiteSpace(spaceId) ? null : spaceId;

      var rows = new List<GuardSensorRow>(limit);
      string? cursorName = string.IsNullOrWhiteSpace(afterName) ? null : afterName;
      string? cursorId = string.IsNullOrWhiteSpace(afterId) ? null : afterId;

      var exhausted = false;
      var scanned = 0;
      while (rows.Count < limit && !exhausted && scanned < MaxScanRows)
      {
        var chunk = await FetchAsync(space, limit + 1, cursorName, cursorId, ct);
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
        return new GuardSensorPage([], false, null, null);

      var complete = await CompleteAsync(rows, caller, ct);
      var hasMore = !exhausted || scanned >= MaxScanRows;
      var last = complete[^1];

      return new GuardSensorPage(
        complete,
        hasMore,
        hasMore ? last.Name : null,
        hasMore ? last.ObjectId : null);
    }

    /// <summary>
    /// Adds what the page needs from outside the tree: the configured mode and alarm flag, the
    /// effective arming, and whether the caller may drive the toggle. All three are batch reads.
    /// </summary>
    private async Task<List<GuardSensorRow>> CompleteAsync(
      List<GuardSensorRow> rows, CallerContext caller, CancellationToken ct)
    {
      var cameraIds = rows
        .Select(r => r.SourceId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
      var settings = await _settings.ListManyAsync(cameraIds, ct);

      var arming = await _arming.ResolveManyAsync(
        rows.Select(r => new ArmingSubject(r.ObjectId, r.SourceId, r.PartKind, r.PartToken))
          .ToList(),
        ct);

      var control = await _rights.AllowedAsync(
        rows.Select(r => r.ObjectId).ToList(), RightActions.Control, caller, ct);

      var result = new List<GuardSensorRow>(rows.Count);
      foreach (var row in rows)
      {
        var configured = settings.GetValueOrDefault(row.SourceId)?.FirstOrDefault(s =>
          string.Equals(s.Kind, row.PartKind, StringComparison.Ordinal)
          && string.Equals(s.Token, row.PartToken, StringComparison.Ordinal));

        var resolved = arming[row.ObjectId];
        result.Add(row with
        {
          ArmMode = configured?.ArmMode ?? Inherit,
          AlarmEnabled = configured?.AlarmEnabled ?? false,
          Configured = configured is not null,
          Armed = resolved.Armed,
          ArmingSource = resolved.Source,
          CanControl = control.Contains(row.ObjectId),
        });
      }

      return result;
    }

    /// <summary>
    /// One keyset page straight from the database, ordered by the sensor's name. A relational
    /// database walks the space's subtree with one CTE; the tests' in-memory provider gets the
    /// same selection in LINQ, because the provider has no SQL to run.
    /// </summary>
    private async Task<List<GuardSensorRow>> FetchAsync(
      string? spaceId,
      int limit,
      string? afterName,
      string? afterId,
      CancellationToken ct)
    {
      return _db.Database.IsRelational()
        ? await FetchRelationalAsync(spaceId, limit, afterName, afterId, ct)
        : await FetchInMemoryAsync(spaceId, limit, afterName, afterId, ct);
    }

    private async Task<List<GuardSensorRow>> FetchRelationalAsync(
      string? spaceId,
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
      var cursorClause = afterName is null
        ? string.Empty
        : "AND (part.name, dn.object_id) > (@after_name, @after_id)";

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
               COALESCE(space.name, '')
        FROM device_node dn
        JOIN objects part ON part.id = dn.object_id
        JOIN camera_placement p ON p.camera_id = dn.source_id AND p.is_reference
        LEFT JOIN geo_objects g ON g.id = p.object_id
        LEFT JOIN objects space ON space.id = g.canvas_id
        LEFT JOIN device_node cam_dn
               ON cam_dn.source_kind = dn.source_kind
              AND cam_dn.source_id = dn.source_id
              AND cam_dn.part_kind = '{DevicePart}'
        LEFT JOIN objects cam ON cam.id = cam_dn.object_id
        WHERE dn.source_kind = '{CameraSource}'
          AND dn.part_kind = '{DigitalInputPart}'
          {spaceClause}
          {cursorClause}
        ORDER BY part.name, dn.object_id
        LIMIT @limit;
        """;

      var result = new List<GuardSensorRow>(limit);
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
        if (afterName is not null)
        {
          cmd.Parameters.Add(new NpgsqlParameter("after_name", afterName));
          cmd.Parameters.Add(new NpgsqlParameter("after_id", Guid.Parse(afterId!)));
        }
        cmd.Parameters.Add(new NpgsqlParameter("limit", limit));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
          result.Add(new GuardSensorRow(
            reader.GetGuid(0).ToString(),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            Inherit,
            false,
            false,
            false,
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

    private async Task<List<GuardSensorRow>> FetchInMemoryAsync(
      string? spaceId,
      int limit,
      string? afterName,
      string? afterId,
      CancellationToken ct)
    {
      var subtree = spaceId is null
        ? null
        : await _map.GetSubtreeIdsAsync([spaceId]);

      var parts = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == CameraSource && n.part_kind == DigitalInputPart)
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
        .ToDictionaryAsync(n => n.source_id, n => n.object_id, StringComparer.Ordinal, ct);

      var rows = new List<GuardSensorRow>();
      foreach (var part in parts)
      {
        if (!placements.TryGetValue(part.source_id, out var placement))
          continue;
        if (subtree is not null && !subtree.Contains(placement.object_id.ToString()))
          continue;

        var name = markers.GetValueOrDefault(part.object_id)?.name ?? part.part_token;
        if (afterName is not null && !IsAfter(name, part.object_id.ToString(), afterName, afterId!))
          continue;

        var spaceIdOfRow = canvases.GetValueOrDefault(placement.object_id);
        var cameraName = cameraNodes.TryGetValue(part.source_id, out var cameraNode)
          ? markers.GetValueOrDefault(cameraNode)?.name ?? string.Empty
          : string.Empty;
        var spaceName = markers.GetValueOrDefault(spaceIdOfRow)?.name ?? string.Empty;

        rows.Add(new GuardSensorRow(
          part.object_id.ToString(),
          part.source_kind,
          part.source_id,
          part.part_kind,
          part.part_token,
          name,
          cameraName,
          spaceIdOfRow.ToString(),
          spaceName,
          Inherit,
          false,
          false,
          false,
          string.Empty,
          false));
      }

      return rows
        .OrderBy(r => r.Name, StringComparer.Ordinal)
        .ThenBy(r => r.ObjectId, StringComparer.Ordinal)
        .Take(limit)
        .ToList();
    }

    private static bool IsAfter(string name, string id, string afterName, string afterId)
    {
      var byName = string.CompareOrdinal(name, afterName);
      return byName > 0 || (byName == 0 && string.CompareOrdinal(id, afterId) > 0);
    }
  }
}
