using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;

namespace VmsMap.Domain.Arming
{
  /// <summary>
  /// A logical device object addressed the way a device card knows it: by the source it belongs
  /// to and the part's token, because the card holds the device, not the part's node id.
  /// `SourceKind` is "camera" today; another kind of device hub joins the same registry under a
  /// kind of its own.
  /// </summary>
  public sealed record DeviceSettingsKey(
    string SourceKind, string SourceId, string PartKind, string PartToken)
  {
    public const string CameraSource = "camera";
    public const string DevicePart = "device";
    public const string DigitalInputPart = "digital_input";
    public const string RelayPart = "relay";

    public bool IsCamera => string.Equals(PartKind, DevicePart, StringComparison.Ordinal);
    public bool IsDigitalInput =>
      string.Equals(PartKind, DigitalInputPart, StringComparison.Ordinal);
  }

  /// <summary>Everything the settings card shows for one logical object.</summary>
  public sealed record DeviceSettingsView(
    string ObjectId,
    DeviceSettingsKey Key,
    string Mode,
    string? TriggerOn,
    bool AlarmEnabled,
    bool Configured,
    NodeArmingInfo? Effective)
  {
    public static readonly IReadOnlyList<string> ArmModes =
      [ArmMode.Inherit, ArmMode.Armed, ArmMode.Disarmed];

    public static readonly IReadOnlyList<string> TriggerModes = ["open", "closed"];
  }

  /// <summary>
  /// The administrative settings of a logical device object: its arming mode and, for a digital
  /// input, the contact state that raises an alarm. The operator's switch stays a command with two
  /// modes; this is where an administrator says who decides it.
  /// </summary>
  public interface IDeviceSettingsService
  {
    Task<DeviceSettingsView?> GetAsync(DeviceSettingsKey key, CancellationToken ct);

    Task<DeviceSettingsView?> SetAsync(
      DeviceSettingsKey key, string mode, string? triggerOn, bool alarmEnabled, string actor,
      CancellationToken ct);

    Task<NodeArmingView?> SetArmingAsync(
      DeviceSettingsKey key, string mode, string actor, CancellationToken ct);
  }

  internal sealed class DeviceSettingsService(
    VmsMapDbContext db,
    DeviceObjectSettingsStore settings,
    EffectiveArmingResolver arming,
    NodeArmingService nodeArming,
    DeviceNodeService nodes,
    ICameraCatalog cameras,
    IDeviceSettingsSource configured,
    CameraArmingReconciler reconciler,
    SensorAlarmService sensorAlarms,
    EventsWriter events,
    ILogger<DeviceSettingsService> logger) : IDeviceSettingsService
  {
    private readonly VmsMapDbContext _db = db ?? throw new ArgumentNullException(nameof(db));
    private readonly DeviceObjectSettingsStore _settings =
      settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly EffectiveArmingResolver _arming =
      arming ?? throw new ArgumentNullException(nameof(arming));
    private readonly NodeArmingService _nodeArming =
      nodeArming ?? throw new ArgumentNullException(nameof(nodeArming));
    private readonly DeviceNodeService _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
    private readonly ICameraCatalog _cameras = cameras ?? throw new ArgumentNullException(nameof(cameras));
    private readonly IDeviceSettingsSource _configured =
      configured ?? throw new ArgumentNullException(nameof(configured));
    private readonly CameraArmingReconciler _reconciler =
      reconciler ?? throw new ArgumentNullException(nameof(reconciler));
    private readonly SensorAlarmService _sensorAlarms =
      sensorAlarms ?? throw new ArgumentNullException(nameof(sensorAlarms));
    private readonly EventsWriter _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<DeviceSettingsService> _logger =
      logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DeviceSettingsView?> GetAsync(DeviceSettingsKey key, CancellationToken ct)
    {
      var node = await FindNodeAsync(key, ct);
      return node is null ? null : await DescribeAsync(key, node, ct);
    }

    /// <summary>
    /// The administrator's save. The mode and the alarm rule are the map's rows; a camera's
    /// effective flag and an input's alarm are re-derived from them at once, so the operator's
    /// panels never show the previous rule. An empty mode leaves the mode as it is: a card that
    /// only edits the rule must not write back a mode it read some time ago.
    /// </summary>
    public async Task<DeviceSettingsView?> SetAsync(
      DeviceSettingsKey key, string mode, string? triggerOn, bool alarmEnabled, string actor,
      CancellationToken ct)
    {
      var error = Validate(key, mode, triggerOn, alarmEnabled);
      if (error is not null)
        throw new ArmingException(error, ArmingRefusal.InvalidRequest);

      var node = await FindNodeAsync(key, ct) ?? await EnsureNodeAsync(key, ct);
      if (node is null)
        return null;

      var configured = await IsConfiguredAsync(key, ct);
      if (!configured)
        throw new ArmingException(
          "The input is not configured yet: an administrator has to add its settings first",
          ArmingRefusal.NotConfigured);

      var before = await _settings.GetAsync(node, ct);
      var actorName = string.IsNullOrWhiteSpace(actor) ? NodeArmingService.SystemActor : actor.Trim();
      var wanted = string.IsNullOrWhiteSpace(mode) ? before.Mode : mode;

      await using var tx = await BeginTransactionAsync(ct);
      await _settings.SetAsync(node, wanted, triggerOn, alarmEnabled, ct);
      if (!string.Equals(before.Mode, wanted, StringComparison.Ordinal))
        await WriteJournalAsync(node, actorName, wanted, ct);
      if (tx is not null)
        await tx.CommitAsync(ct);

      // A camera's flag and an input's alarm follow from the settings at once; a failure here is
      // reported but does not undo the save — the next snapshot, event or start repairs it.
      try
      {
        if (key.IsCamera)
          await _reconciler.ReconcileAsync(key.SourceId, ct);
        else
          await _sensorAlarms.ReconcileCameraAsync(key.SourceId, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        _logger.LogWarning(ex, "Applying the settings of '{SourceId}/{Token}' failed", key.SourceId, key.PartToken);
      }

      return await DescribeAsync(key, node, ct);
    }

    /// <summary>The operator's switch, addressed by source: two modes, the map's facade writes the rest.</summary>
    public async Task<NodeArmingView?> SetArmingAsync(
      DeviceSettingsKey key, string mode, string actor, CancellationToken ct)
    {
      var node = await FindNodeAsync(key, ct);
      if (node is null)
        throw new ArmingException(
          "The part has no node in the tree yet: it appears with its first snapshot or event",
          ArmingRefusal.NotFound);

      return await _nodeArming.SetAsync(node, mode, actor, ct);
    }

    /// <summary>The node the settings live on: the canonical camera or part, never a replica.</summary>
    private async Task<string?> FindNodeAsync(DeviceSettingsKey key, CancellationToken ct)
    {
      var objectId = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == key.SourceKind
                 && n.source_id == key.SourceId
                 && n.part_kind == key.PartKind
                 && n.part_token == key.PartToken)
        .Select(n => (Guid?)n.object_id)
        .FirstOrDefaultAsync(ct);

      return objectId is { } id ? Utils.ConvertGuidToObjectId(id) : null;
    }

    /// <summary>
    /// Creates the node the settings belong to. The adapter makes a device's node from its
    /// configuration, so an administrator can set a part's behaviour before it has reported.
    /// </summary>
    private async Task<string?> EnsureNodeAsync(DeviceSettingsKey key, CancellationToken ct)
    {
      if (!string.Equals(key.SourceKind, DeviceSettingsKey.CameraSource, StringComparison.Ordinal))
        return null;

      if (key.IsCamera)
      {
        var camera = await _cameras.GetAsync(key.SourceId, ct);
        if (camera is null)
          return null;

        await _nodes.EnsureCameraAsync(camera, ct);
        return CameraObjectId.For(key.SourceId);
      }

      return await _nodes.EnsureAsync(key.SourceKind, key.SourceId, key.PartKind, key.PartToken, ct);
    }

    private async Task<DeviceSettingsView> DescribeAsync(
      DeviceSettingsKey key, string objectId, CancellationToken ct)
    {
      var own = await _settings.GetAsync(objectId, ct);
      var effective = await _arming.ResolveAsync(objectId, ct);
      return new DeviceSettingsView(
        objectId, key, own.Mode, own.TriggerOn, own.AlarmEnabled,
        await IsConfiguredAsync(key, ct), effective);
    }

    private async Task<bool> IsConfiguredAsync(DeviceSettingsKey key, CancellationToken ct)
    {
      if (key.IsCamera)
        return true;

      var row = await _configured.FindAsync(
        key.SourceId, key.PartKind, key.PartToken, ct);
      return row is not null;
    }

    private static string? Validate(
      DeviceSettingsKey key, string mode, string? triggerOn, bool alarmEnabled)
    {
      if (!string.Equals(key.SourceKind, DeviceSettingsKey.CameraSource, StringComparison.Ordinal))
        return $"source_kind must be '{DeviceSettingsKey.CameraSource}'";
      if (string.IsNullOrWhiteSpace(key.SourceId))
        return "source_id must not be empty";
      // Empty means "leave it alone": the administrator's card that edits only the alarm rule says
      // nothing about the mode, and must not be able to undo a change made after it loaded.
      if (!string.IsNullOrWhiteSpace(mode) && !ArmMode.IsKnown(mode))
        return "arm_mode must be 'inherit', 'armed' or 'disarmed'";

      if (key.IsCamera)
      {
        if (!string.IsNullOrWhiteSpace(key.PartToken))
          return "a camera's part_token must be empty";
        if (!string.IsNullOrWhiteSpace(triggerOn) || alarmEnabled)
          return "a camera carries no alarm rule";
        return null;
      }

      if (!key.IsDigitalInput)
        return $"'{key.PartKind}' carries no arming or alarm settings";
      if (string.IsNullOrWhiteSpace(key.PartToken))
        return "part_token must not be empty";

      var hasTrigger = !string.IsNullOrWhiteSpace(triggerOn);
      if (hasTrigger && triggerOn is not ("open" or "closed"))
        return "trigger_on must be 'open', 'closed' or empty";
      if (alarmEnabled && !hasTrigger)
        return "an alarm needs the contact state it follows";

      return null;
    }

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(
      CancellationToken ct) =>
      _db.Database.IsRelational()
        ? _db.Database.BeginTransactionAsync(ct)!
        : Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);

    private Task WriteJournalAsync(string objectId, string actor, string mode, CancellationToken ct) =>
      _events.WriteAsync([
        new EventDTO
        {
          id = Guid.NewGuid().ToString(),
          object_id = objectId,
          event_name = NodeArmingService.JournalEventName,
          event_priority = 0,
          timestamp = DateTime.UtcNow,
          param0 = actor,
          param1 = mode,
        }
      ], ct);
  }
}
