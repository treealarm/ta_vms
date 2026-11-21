using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Outbox;

namespace VmsMap.Domain.Arming
{
  /// <summary>Everything one arming read answers: whether the node takes part, what it is set to,
  /// and what it actually resolves to. `Arming` is null when the node has no arming at all.</summary>
  public sealed record NodeArmingView(
    bool Found,
    bool Armable,
    bool Configured,
    string Mode,
    IReadOnlyList<string> Modes,
    NodeArmingInfo? Arming);

  public enum ArmingRefusal
  {
    /// <summary>The request itself is wrong: a bad id, a mode this node does not take.</summary>
    InvalidRequest,
    /// <summary>The node kind has no arming at all — a relay, a landmark, a zone nothing stands in.</summary>
    NotArmable,
    /// <summary>A digital input the administrator has not configured: there is no mode to write.</summary>
    NotConfigured,
    NotFound,
  }

  public sealed class ArmingException(string message, ArmingRefusal refusal) : Exception(message)
  {
    public ArmingRefusal Refusal { get; } = refusal;
  }

  /// <summary>
  /// The one write behind every guard switch, and the read that goes with it. A device delegates to
  /// VmsCfg, which owns its arming column; a container keeps the `armed` property on its own node.
  /// The map's journal and the outbox row live in the same transaction as that property, so the
  /// operator's action and the re-check of the sensors are one commit.
  /// <para>
  /// The read resolves a replica to its owner first: a picture of a camera or a sensor on a plan is
  /// the same device, and the map's inspector selects the node that is drawn.
  /// </para>
  /// </summary>
  public sealed class NodeArmingService
  {
    public const string JournalEventName = "arming";
    public const string SystemActor = "system";

    private readonly VmsMapDbContext _db;
    private readonly EffectiveArmingResolver _arming;
    private readonly IDeviceSettingsSource _settings;
    private readonly DeviceObjectSettingsStore _objectSettings;
    private readonly DeviceArmingDerivation _derivation;
    private readonly EventsWriter _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<NodeArmingService> _logger;

    public NodeArmingService(
      VmsMapDbContext db,
      EffectiveArmingResolver arming,
      IDeviceSettingsSource settings,
      DeviceObjectSettingsStore objectSettings,
      DeviceArmingDerivation derivation,
      EventsWriter events,
      TimeProvider clock,
      ILogger<NodeArmingService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _arming = arming ?? throw new ArgumentNullException(nameof(arming));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _objectSettings = objectSettings ?? throw new ArgumentNullException(nameof(objectSettings));
      _derivation = derivation ?? throw new ArgumentNullException(nameof(derivation));
      _events = events ?? throw new ArgumentNullException(nameof(events));
      _clock = clock ?? throw new ArgumentNullException(nameof(clock));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeArmingView?> GetAsync(string objectId, CancellationToken ct)
    {
      var node = await ResolveAsync(objectId, ct);
      if (node is null)
        return null;

      return await DescribeAsync(node, ct);
    }

    public async Task<NodeArmingView?> SetAsync(
      string objectId, string mode, string actor, CancellationToken ct, DateTime? until = null)
    {
      if (!ArmMode.IsKnown(mode))
        throw new ArmingException($"'{mode}' is not an arming mode", ArmingRefusal.InvalidRequest);

      if (until is { } deadline)
      {
        if (!string.Equals(mode, ArmMode.Disarmed, StringComparison.Ordinal))
          throw new ArmingException(
            "Only a disarm can carry a bypass deadline", ArmingRefusal.InvalidRequest);
        if (deadline.Kind != DateTimeKind.Utc)
          deadline = deadline.ToUniversalTime();
        if (deadline <= _clock.GetUtcNow().UtcDateTime)
          throw new ArmingException(
            "The bypass deadline must be in the future", ArmingRefusal.InvalidRequest);
        until = deadline;
      }

      var node = await ResolveAsync(objectId, ct);
      if (node is null)
        throw new ArmingException("No such node", ArmingRefusal.NotFound);

      var armability = await ArmabilityAsync(node, ct);
      if (!armability.Armable)
        throw new ArmingException("This node cannot be armed", ArmingRefusal.NotArmable);
      if (!armability.Accepts(mode))
        throw new ArmingException($"A {ArmKind(node)} takes no '{mode}' mode", ArmingRefusal.InvalidRequest);
      if (!armability.Configured && node.DeviceKind == DeviceObjectId.DigitalInputPart)
        throw new ArmingException(
          "The input is not configured yet: an administrator has to add its settings first",
          ArmingRefusal.NotConfigured);

      var actorName = string.IsNullOrWhiteSpace(actor) ? SystemActor : actor.Trim();

      if (node.DeviceKind is { } deviceKind)
      {
        await SetDeviceAsync(node, deviceKind, mode, actorName, until, ct);
      }
      else
      {
        await SetContainerAsync(node, mode, actorName, until, ct);
      }

      return await GetAsync(objectId, ct);
    }

    private async Task SetDeviceAsync(
      Node node, string deviceKind, string mode, string actor, DateTime? until,
      CancellationToken ct)
    {
      // The mode is the map's own row, so the read, the write and its journal are one transaction:
      // a concurrent change to the deadline cannot be lost between reading the own mode and
      // overwriting it.
      await using var tx = await BeginTransactionAsync(ct);

      // The deadline is a modifier of the explicit disarm; the own mode to restore is read before
      // the write, because the write is what overwrites it.
      var current = await _objectSettings.GetAsync(node.CanonicalId, ct);
      var restoreMode = RestoreModeFor(current, mode, until);

      await _objectSettings.SetArmingAsync(node.CanonicalId, mode, until, restoreMode, ct);
      await WriteJournalAsync(node.CanonicalId, actor, mode, until, expired: false, ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      // Every write path derives once: a camera's flag for VmsCfg and the sensors' alarm re-check.
      // It is outside the transaction by design — the services near the hardware cannot see the
      // map, and a failure here is repaired by the next snapshot, event or start.
      await _derivation.DeriveAsync(deviceKind, node.SourceId, ct);
    }

    /// <summary>
    /// The own mode to write back when the deadline passes. A node that already carried a timed
    /// exclusion keeps the intent it was given; otherwise an explicit arm is restored as `armed`
    /// and everything else returns to `inherit`. Extending a deadline must not silently change
    /// an armed room into a zone-following one.
    /// </summary>
    private static string? RestoreModeFor(
      DeviceObjectSettings current, string mode, DateTime? until)
    {
      if (until is null || !string.Equals(mode, ArmMode.Disarmed, StringComparison.Ordinal))
        return null;

      if (string.Equals(current.Mode, ArmMode.Disarmed, StringComparison.Ordinal)
          && current.BypassUntil is not null)
      {
        return current.RestoreMode ?? ArmMode.Inherit;
      }

      return string.Equals(current.Mode, ArmMode.Armed, StringComparison.Ordinal)
        ? ArmMode.Armed
        : ArmMode.Inherit;
    }

    private async Task SetContainerAsync(
      Node node, string mode, string actor, DateTime? until, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(node.CanonicalId)!.Value;
      await using var tx = await BeginTransactionAsync(ct);

      var stored = await _db.ObjectArming.AsNoTracking()
        .Where(a => a.object_id == guid)
        .Select(a => new ContainerArmingRow(a.armed, a.bypass_until, a.restore_mode))
        .FirstOrDefaultAsync(ct);

      var value = ArmMode.Value(mode);
      var effectiveChanged = value is null ? stored is not null : stored?.Armed != value;
      var restoreMode = RestoreModeFor(stored, mode, until);
      // A new deadline on an already disarmed node changes the row without changing the effective
      // answer: it needs the write and the journal, but not the alarm re-check the outbox triggers.
      var timerChanged = until != stored?.BypassUntil || restoreMode != stored?.RestoreMode;
      var rowChanged = effectiveChanged || timerChanged;

      if (rowChanged)
        await WriteArmingAsync(guid, value, until, restoreMode, ct);

      // The journal records the action, not the result: a repeated command that changed nothing is
      // still an operator's action and stays in the record (M2.4i).
      await WriteJournalAsync(node.CanonicalId, actor, mode, until, expired: false, ct);

      // Only a real change can raise an alarm: disarming closes none, and a repeated command
      // changes nothing. The row is in the same transaction as the property it announces.
      if (effectiveChanged)
      {
        _db.MapOutbox.Add(new DBMapOutbox
        {
          topic = MapTopics.ArmingChanged,
          payload = JsonSerializer.Serialize(new { @object_id = node.CanonicalId }),
          created_at = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
      }

      if (tx is not null)
        await tx.CommitAsync(ct);

      if (effectiveChanged)
      {
        _logger.LogInformation(
          "Node '{ObjectId}' set to '{Mode}' by '{Actor}'", node.CanonicalId, mode, actor);
      }
    }

    private sealed record ContainerArmingRow(bool Armed, DateTime? BypassUntil, string? RestoreMode);

    /// <summary>
    /// The own mode to restore for a container. A node that already carried a timed exclusion
    /// keeps the intent it was given; an explicitly armed room goes back to `armed`, and a node
    /// that followed its parent returns to `inherit`.
    /// </summary>
    private static string? RestoreModeFor(
      ContainerArmingRow? stored, string mode, DateTime? until)
    {
      if (until is null || !string.Equals(mode, ArmMode.Disarmed, StringComparison.Ordinal))
        return null;

      if (stored is { Armed: false, BypassUntil: not null })
        return stored.RestoreMode ?? ArmMode.Inherit;

      return stored?.Armed == true ? ArmMode.Armed : ArmMode.Inherit;
    }

    private async Task WriteArmingAsync(
      Guid guid, bool? armed, DateTime? until, string? restoreMode, CancellationToken ct)
    {
      var row = await _db.ObjectArming.FirstOrDefaultAsync(a => a.object_id == guid, ct);

      if (armed is null)
      {
        // Inherit is the absence of the row: a reader must not see "follows its parent" as an
        // explicit state, and no row is what the resolver takes as "ask above".
        if (row is not null) _db.ObjectArming.Remove(row);
      }
      else if (row is null)
      {
        _db.ObjectArming.Add(new DBObjectArming
        {
          object_id = guid,
          armed = armed.Value,
          bypass_until = until,
          restore_mode = restoreMode,
        });
      }
      else
      {
        row.armed = armed.Value;
        row.bypass_until = until;
        row.restore_mode = restoreMode;
      }

      await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Restores a node whose bypass deadline has passed: writes the own mode it had before the
    /// bypass, clears the deadline, journals the automatic action, and re-derives afterwards.
    /// Returns false when nothing was due (another replica may have already done it).
    /// </summary>
    public async Task<bool> ExpireBypassAsync(string objectId, CancellationToken ct)
    {
      var node = await ResolveAsync(objectId, ct);
      if (node is null)
        return false;

      var now = _clock.GetUtcNow().UtcDateTime;
      if (node.DeviceKind is { } deviceKind)
        return await ExpireDeviceAsync(node, deviceKind, now, ct);
      return await ExpireContainerAsync(node, now, ct);
    }

    private async Task<bool> ExpireDeviceAsync(
      Node node, string deviceKind, DateTime now, CancellationToken ct)
    {
      var current = await _objectSettings.GetAsync(node.CanonicalId, ct);
      if (current.BypassUntil is not { } until || until > now)
        return false;

      var restoreMode = current.RestoreMode ?? ArmMode.Inherit;

      await using var tx = await BeginTransactionAsync(ct);
      await _objectSettings.SetArmingAsync(node.CanonicalId, restoreMode, null, null, ct);
      await WriteJournalAsync(
        node.CanonicalId, SystemActor, restoreMode, until, expired: true, ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      await _derivation.DeriveAsync(deviceKind, node.SourceId, ct);
      _logger.LogInformation(
        "Bypass of '{ObjectId}' expired: restored '{Mode}'", node.CanonicalId, restoreMode);
      return true;
    }

    private async Task<bool> ExpireContainerAsync(Node node, DateTime now, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(node.CanonicalId)!.Value;
      var stored = await _db.ObjectArming.AsNoTracking()
        .Where(a => a.object_id == guid)
        .Select(a => new ContainerArmingRow(a.armed, a.bypass_until, a.restore_mode))
        .FirstOrDefaultAsync(ct);

      if (stored is null || stored.BypassUntil is not { } until || until > now)
        return false;

      var restoreMode = stored.RestoreMode ?? ArmMode.Inherit;
      var value = ArmMode.Value(restoreMode);

      await using var tx = await BeginTransactionAsync(ct);

      // The row definitely leaves its bypassed state, so the subscribers must re-derive the
      // devices under this node: an open contact has to alarm as soon as the exclusion ends.
      await WriteArmingAsync(guid, value, null, null, ct);
      await WriteJournalAsync(
        node.CanonicalId, SystemActor, restoreMode, until, expired: true, ct);
      _db.MapOutbox.Add(new DBMapOutbox
      {
        topic = MapTopics.ArmingChanged,
        payload = JsonSerializer.Serialize(new { @object_id = node.CanonicalId }),
        created_at = DateTime.UtcNow,
      });
      await _db.SaveChangesAsync(ct);

      if (tx is not null)
        await tx.CommitAsync(ct);

      _logger.LogInformation(
        "Bypass of '{ObjectId}' expired: restored '{Mode}'", node.CanonicalId, restoreMode);
      return true;
    }

    private Task WriteJournalAsync(
      string canonicalId, string actor, string mode, DateTime? until, bool expired,
      CancellationToken ct) =>
      _events.WriteAsync([
        new EventDTO
        {
          id = Guid.NewGuid().ToString(),
          object_id = canonicalId,
          event_name = JournalEventName,
          event_priority = 0,
          timestamp = DateTime.UtcNow,
          param0 = actor,
          param1 = mode,
          extra_props = JournalFacts(until, expired),
        }
      ], ct);

    /// <summary>The deadline and the automatic return are the stored facts the journal keeps; the
    /// operator's actor and mode stay in `param0`/`param1`, as every arming entry has them.</summary>
    private static List<ObjExtraPropertyDTO>? JournalFacts(DateTime? until, bool expired)
    {
      if (until is null && !expired)
        return null;

      var props = new List<ObjExtraPropertyDTO>();
      if (until is { } deadline)
      {
        props.Add(new ObjExtraPropertyDTO
        {
          prop_name = "until",
          str_val = deadline.ToUniversalTime().ToString("O"),
        });
      }

      if (expired)
        props.Add(new ObjExtraPropertyDTO { prop_name = "expired", str_val = "true" });

      return props;
    }

    private async Task<NodeArmingView> DescribeAsync(Node node, CancellationToken ct)
    {
      var armability = await ArmabilityAsync(node, ct);

      if (node.DeviceKind is { } deviceKind)
      {
        if (string.Equals(deviceKind, DeviceObjectId.DevicePart, StringComparison.Ordinal))
        {
          // A camera resolves like a sensor: its own mode, else the zone above its reference
          // replica. Its flag is what the map derives from that, never an input to it.
          var camera = await _arming.ResolveAsync(node.CanonicalId, ct)
            ?? new NodeArmingInfo(node.CanonicalId, false, EffectiveArmingResolver.None);
          return new NodeArmingView(
            true, armability.Armable, armability.Configured,
            camera.Mode,
            armability.Modes,
            camera);
        }

        if (!string.Equals(deviceKind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal))
        {
          // A relay is driven, not watched: it has no arming to report.
          return new NodeArmingView(true, false, armability.Configured, string.Empty, [], null);
        }

        var own = await _objectSettings.GetAsync(node.CanonicalId, ct);
        var resolved = await _arming.ResolveAsync(node.CanonicalId, ct);
        return new NodeArmingView(
          true, true, armability.Configured,
          own.Mode,
          armability.Modes,
          resolved ?? new NodeArmingInfo(node.CanonicalId, false, EffectiveArmingResolver.None));
      }

      var containerArming = await _arming.ResolveSpaceAsync(node.CanonicalId, ct)
        ?? new NodeArmingInfo(node.CanonicalId, false, EffectiveArmingResolver.None);
      var ownMode = await CurrentModeAsync(node.CanonicalId, ct);
      return new NodeArmingView(
        true, armability.Armable, armability.Configured, ownMode, armability.Modes, containerArming);
    }

    /// <summary>The mode written on the node itself, or `inherit` when it carries no property.</summary>
    private async Task<string> CurrentModeAsync(string canonicalId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(canonicalId);
      if (guid is null)
        return ArmMode.Inherit;

      var row = await _db.ObjectArming.AsNoTracking()
        .Where(a => a.object_id == guid.Value)
        .Select(a => new { a.armed })
        .FirstOrDefaultAsync(ct);

      if (row is null)
        return ArmMode.Inherit;
      return row.armed ? ArmMode.Armed : ArmMode.Disarmed;
    }

    private async Task<Armability> ArmabilityAsync(Node node, CancellationToken ct)
    {
      if (node.DeviceKind is { } deviceKind)
      {
        var configured = string.Equals(deviceKind, DeviceObjectId.DevicePart, StringComparison.Ordinal)
          || await IsConfiguredAsync(node, ct);
        return ArmingPolicy.ForPart(deviceKind, configured) ?? Armability.NotArmable;
      }

      // A space is a place by definition; anything else is a zone only when devices actually
      // stand through it, because the resolver walks up from a camera's reference replica.
      if (node.Kind == NodeKind.Space)
        return Armability.Container();

      var hasDevices = node.Kind is NodeKind.Figure or NodeKind.Object or NodeKind.Diagram
        && await HasReferencePlacementAsync(node.Guid, ct);
      return ArmingPolicy.ForContainer(node.Kind, hasDevices);
    }

    private async Task<bool> IsConfiguredAsync(Node node, CancellationToken ct)
    {
      var settings = await _settings.FindAsync(
        node.SourceId, DeviceObjectId.DigitalInputPart, node.PartToken, ct);
      return settings is not null;
    }

    /// <summary>Whether any camera's reference replica stands inside this node's subtree — the
    /// test that separates a zone from a picture. One query per tree level, stopping at the first
    /// answer.</summary>
    private async Task<bool> HasReferencePlacementAsync(Guid root, CancellationToken ct)
    {
      var visited = new HashSet<Guid>();
      var frontier = new List<Guid> { root };

      while (frontier.Count > 0)
      {
        if (await _db.CameraPlacements.AsNoTracking()
              .AnyAsync(p => p.is_reference && frontier.Contains(p.object_id), ct))
        {
          return true;
        }

        var level = frontier.Where(visited.Add).ToList();
        if (level.Count == 0)
          break;

        // Children only: a replica is not a child, and a picture standing elsewhere says nothing
        // about what stands here.
        frontier = await _db.Markers.AsNoTracking()
          .Where(m => m.parent_id.HasValue && level.Contains(m.parent_id.Value))
          .Select(m => m.id)
          .ToListAsync(ct);
      }

      return false;
    }

    private static string ArmKind(Node node) =>
      node.DeviceKind == DeviceObjectId.DigitalInputPart ? "sensor" : "container";

    private Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken ct)
    {
      // A caller that already holds one — the expiry sweep claims a batch under a transaction —
      // keeps it: the claim, the write and the clearing of the deadline must commit together.
      if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        return Task.FromResult<IDbContextTransaction?>(null);
      return _db.Database.BeginTransactionAsync(ct)!;
    }

    /// <summary>A node as this service sees it: the canonical one (a replica resolves through its
    /// owner), its kind, and the device registry row when it is a device or a part.</summary>
    private sealed record Node(
      string CanonicalId, Guid Guid, NodeKind Kind, string? DeviceKind, string SourceId, string PartToken);

    private async Task<Node?> ResolveAsync(string objectId, CancellationToken ct)
    {
      var guid = Utils.ConvertObjectIdToGuid(objectId);
      if (guid is null)
        return null;

      var marker = await _db.Markers.AsNoTracking()
        .FirstOrDefaultAsync(m => m.id == guid.Value, ct);
      if (marker is null)
        return null;

      var canonicalGuid = marker.owner_id ?? marker.id;
      var canonicalId = Utils.ConvertGuidToObjectId(canonicalGuid);
      var kinds = await NodeKindLookup.ResolveAsync(_db, [canonicalId], ct);
      var kind = kinds.GetValueOrDefault(canonicalId, NodeKind.Object);

      var device = await _db.DeviceNodes.AsNoTracking()
        .FirstOrDefaultAsync(n => n.object_id == canonicalGuid, ct);

      return new Node(
        canonicalId,
        canonicalGuid,
        kind,
        device?.part_kind,
        device?.source_id ?? string.Empty,
        device?.part_token ?? string.Empty);
    }
  }
}
