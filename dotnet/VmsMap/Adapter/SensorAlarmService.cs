using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
    private readonly IDeviceSettingsSource _settings;
    private readonly EffectiveArmingResolver _arming;
    private readonly AlarmStore _alarms;

    public SensorAlarmService(
      VmsMapDbContext db,
      IDeviceSettingsSource settings,
      EffectiveArmingResolver arming,
      AlarmStore alarms)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _arming = arming ?? throw new ArgumentNullException(nameof(arming));
      _alarms = alarms ?? throw new ArgumentNullException(nameof(alarms));
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
      var settings = await _settings.FindAsync(cameraId, partKind, token, ct);

      var alarmEnabled = settings is { AlarmEnabled: true } && settings.TriggerOn is not null;
      var triggers = alarmEnabled
        && contact is not null
        && string.Equals(contact, settings!.TriggerOn, StringComparison.Ordinal);

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
      var settings = await _settings.ListAsync(cameraId, ct);
      if (settings.Count == 0)
        return;

      var nodes = await _db.DeviceNodes.AsNoTracking()
        .Where(n => n.source_kind == DeviceObjectId.CameraSource
                    && n.source_id == cameraId
                    && n.part_kind == DeviceObjectId.DigitalInputPart)
        .Select(n => new { n.object_id, n.part_token })
        .ToListAsync(ct);
      if (nodes.Count == 0)
        return;

      var ids = nodes.Select(n => n.object_id).ToList();
      var stored = await _db.ObjectStates.AsNoTracking()
        .Where(s => ids.Contains(s.id))
        .Select(s => new { s.id, states = s.states.Select(v => v.state).ToList() })
        .ToListAsync(ct);
      var stateById = stored.ToDictionary(
        s => s.id,
        s => s.states.FirstOrDefault(st => st == SensorOpen || st == SensorClosed));

      foreach (var node in nodes)
      {
        // No row for this token means the defaults, and the default is "no alarm": the reconcile
        // below would then return the condition to normal, which is right but pointless work.
        if (!settings.Any(s =>
              string.Equals(s.Kind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal)
              && string.Equals(s.Token, node.part_token, StringComparison.Ordinal)))
          continue;

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
  }
}
