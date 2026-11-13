using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Adapter
{
  /// <summary>
  /// The rule that turns a digital input's contact into an alarm. One place, because three paths
  /// reach it — a transition, a snapshot, and the settings that were just changed — and they must
  /// agree: any of them may be the one that notices a condition that is live but has no alarm.
  /// </summary>
  /// <remarks>
  /// The condition is a contact equal to the configured <c>trigger_on</c>, on an input whose
  /// <c>alarm_enabled</c> is set and whose node is effectively armed. Every path ends in the same
  /// two calls, and both are idempotent: <see cref="AlarmStore.RaiseAsync"/> by the dedup key,
  /// <see cref="AlarmStore.NormalAsync"/> by the row. That is what lets a lost message, a restarted
  /// VmsMap or a restarted VmsOnvif be repaired by the next snapshot instead of being remembered.
  /// <para>
  /// An unknown polarity raises nothing: the contact is written and shown, but which of its values
  /// means "touched" is unknown until the device reports an idle state (invariant 5). Disarming
  /// never closes an alarm — only the condition leaving does.
  /// </para>
  /// </remarks>
  internal sealed class SensorAlarmService
  {
    /// <summary>What the raise records as its trigger when arming found the condition already true.</summary>
    public const string ArmedWithActiveCondition = "armed_with_active_condition";

    public const string SensorOpen = DeviceEventTranslator.SensorOpen;
    public const string SensorClosed = DeviceEventTranslator.SensorClosed;

    private readonly VmsMapDbContext _db;
    private readonly DeviceObjectSettingsStore _settings;
    private readonly EffectiveArmingResolver _arming;
    private readonly AlarmStore _alarms;
    private readonly IMapService _map;
    private readonly ILogger<SensorAlarmService> _logger;

    public SensorAlarmService(
      VmsMapDbContext db,
      DeviceObjectSettingsStore settings,
      EffectiveArmingResolver arming,
      AlarmStore alarms,
      IMapService map,
      ILogger<SensorAlarmService> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _arming = arming ?? throw new ArgumentNullException(nameof(arming));
      _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The key one part's alarm is deduplicated by, whatever path raised it.</summary>
    public static string DedupKey(string sourceKind, string sourceId, string partKind, string token) =>
      $"sensor:{sourceKind}:{sourceId}:{partKind}:{token}";

    /// <summary>
    /// Applies the rule to one part: raise when the contact matches the trigger on an armed input,
    /// return to normal when it does not. <paramref name="contact"/> is null when the polarity is
    /// unknown, and then nothing is raised or cleared.
    /// </summary>
    public async Task ReconcileAsync(
      Guid objectId,
      string cameraId,
      string partKind,
      string token,
      string? contact,
      string? trigger = null,
      CancellationToken ct = default)
    {
      // A relay is driven, not watched: its coil is not a condition anybody acknowledges.
      if (!DevicePayload.IsDigitalInput(partKind))
        return;

      var dedupKey = DedupKey(DeviceObjectId.CameraSource, cameraId, partKind, token);
      var settings = await _settings.GetAsync(Utils.ConvertGuidToObjectId(objectId), ct);

      var alarmEnabled = settings.AlarmEnabled && settings.TriggerOn is not null;
      var triggers = alarmEnabled
        && contact is not null
        && string.Equals(contact, settings.TriggerOn, StringComparison.Ordinal);

      if (!triggers)
      {
        // The condition is not live — either the contact is at rest, the input's alarm is off, or
        // its polarity is unknown. Nothing to do unless an alarm is open, which the next snapshot
        // or event would otherwise keep alive. The check comes first so a snapshot that changes
        // nothing does not take the map's global locks: at a hundred thousand cameras, a sweep
        // that did would serialise the whole deployment against itself.
        var open = await OpenAlarmAsync(objectId, dedupKey, ct);
        if (open is null)
          return;

        // A condition whose polarity is unknown must not clear an alarm either: the device has not
        // said which contact means "touched", so the last known truth stands.
        if (contact is null && alarmEnabled)
          return;

        await _alarms.NormalAsync(objectId, dedupKey, ct);
        return;
      }

      // The condition is live. An alarm that is already live needs nothing; a returned one has to
      // come back, and a missing one has to be raised if the input is on guard.
      var current = await OpenAlarmAsync(objectId, dedupKey, ct);
      if (current is { condition_active: true, status: not AlarmStatus.Returned })
        return;

      var armed = (await _arming.ResolveAsync(Utils.ConvertGuidToObjectId(objectId), ct))?.Armed
        ?? false;
      if (!armed)
        return;

      await _alarms.RaiseAsync(new RaiseAlarmSpec(
        ObjectId: objectId,
        Source: AlarmSource.Sensor,
        DedupKey: dedupKey,
        Trigger: trigger ?? contact), ct);
    }

    /// <summary>The open alarm for one part's condition, if there is one.</summary>
    private Task<DBAlarm?> OpenAlarmAsync(
      Guid objectId, string dedupKey, CancellationToken ct) =>
      _db.Alarms.AsNoTracking()
        .Where(a => a.object_id == objectId
                    && a.dedup_key == dedupKey
                    && a.status != AlarmStatus.Closed)
        .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Re-applies the rule to every input of a camera from the state already stored. Called after
    /// a settings change: a contact that was in its trigger position while the input was disarmed
    /// must raise the moment the input is armed, without waiting for the device to say anything.
    /// </summary>
    public async Task ReconcileCameraAsync(string cameraId, CancellationToken ct)
    {
      var nodes = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == DeviceObjectId.CameraSource
                    && n.source_id == cameraId
                    && n.part_kind == DeviceObjectId.DigitalInputPart)
        .Select(n => new { n.object_id, n.part_token })
        .ToListAsync(ct);
      if (nodes.Count == 0)
        return;

      // A part with no alarm has nothing to reconcile: the default rule raises nothing, and the
      // reconcile below would only return a condition that is already normal.
      var settings = await _settings.GetManyAsync(
        nodes.Select(n => Utils.ConvertGuidToObjectId(n.object_id)).ToList(), ct);
      var withAlarm = nodes
        .Where(n => settings[Utils.ConvertGuidToObjectId(n.object_id)] is
          { AlarmEnabled: true, TriggerOn: not null })
        .ToList();
      if (withAlarm.Count == 0)
        return;

      var ids = withAlarm.Select(n => n.object_id).ToList();
      var stored = await _db.ObjectStates.AsNoTracking()
        .Where(s => ids.Contains(s.id))
        .Select(s => new { s.id, states = s.states.Select(v => v.state).ToList() })
        .ToListAsync(ct);
      var stateById = stored.ToDictionary(
        s => s.id,
        s => s.states.FirstOrDefault(st => st == SensorOpen || st == SensorClosed));

      foreach (var node in withAlarm)
      {
        var contact = stateById.GetValueOrDefault(node.object_id) switch
        {
          SensorOpen => "open",
          SensorClosed => "closed",
          _ => null,
        };

        await ReconcileAsync(
          node.object_id, cameraId, DeviceObjectId.DigitalInputPart, node.part_token,
          contact, ArmedWithActiveCondition, ct);
      }
    }

    /// <summary>
    /// The same rule over a whole zone: arming a floor or a building changes the effective arming
    /// of every sensor whose camera's <b>reference</b> replica stands under it, and a contact
    /// already in its trigger position must raise now. Only cameras that really stand here count —
    /// a picture of the same camera on another plan has its own ancestors.
    /// <para>
    /// Disarming needs none of this: only the condition leaving closes an alarm.
    /// </para>
    /// </summary>
    public async Task ReconcileSpaceAsync(string spaceId, CancellationToken ct)
    {
      var subtree = await _map.GetSubtreeIdsAsync([spaceId]);
      if (subtree.Count == 0)
        return;

      var guids = subtree
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var cameraIds = await _db.CameraPlacements.AsNoTracking()
        .Where(p => p.is_reference && guids.Contains(p.object_id))
        .Select(p => p.camera_id)
        .Distinct()
        .ToListAsync(ct);

      // One alarm at a time: the store takes the map's global locks, so a batch that keeps a
      // hundred of them open would serialise the deployment against itself.
      foreach (var chunk in cameraIds.Chunk(200))
      {
        foreach (var cameraId in chunk)
        {
          try
          {
            await ReconcileCameraAsync(cameraId, ct);
          }
          catch (OperationCanceledException) when (ct.IsCancellationRequested)
          {
            throw;
          }
          catch (Exception ex)
          {
            // One camera failing must not leave the rest of the zone unarmed in a browser that
            // was told the zone is on guard; the next snapshot retries this one anyway.
            _logger.LogWarning(ex,
              "Re-checking the sensors of camera '{CameraId}' after a zone's arming failed", cameraId);
          }
        }
      }
    }
  }
}
