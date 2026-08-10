using Dapr.Client;
using Vms.Cfg;

namespace VmsCfg;

/// In-memory snapshot of all recording schedules and their camera assignments.
///
/// ListCameras/GetCamera resolve schedule_active on every read, so that path must not touch the
/// database — hence a cache rather than a query. It is per replica, so the copy that did not handle
/// an edit has to learn about it somehow: VmsCfg writes a new value to the shared schedule-version
/// key on every edit, and each replica drops its snapshot when that value moves. Deliberately not
/// pub/sub — all replicas share one app id and therefore one consumer group, so an event would reach
/// exactly one of them and the rest would keep answering with the old windows.
public sealed class ScheduleCache(ScheduleDb scheduleDb, DaprClient dapr, ILogger<ScheduleCache> logger)
{
  public const string VersionKey = "schedule-version";

  private static readonly TimeSpan VersionProbeInterval = TimeSpan.FromSeconds(5);
  /// Backstop for a state store that cannot be read: the snapshot is rebuilt at least this often
  /// even if the version can never be fetched, so an infrastructure fault degrades freshness
  /// instead of freezing it.
  private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private readonly SemaphoreSlim _refreshLock = new(1, 1);
  private DateTime _versionCheckedAtUtc = DateTime.MinValue;
  private DateTime _reloadedAtUtc = DateTime.MinValue;
  private string? _lastSeenVersion;

  private sealed record Snapshot(
    IReadOnlyDictionary<Guid, RecordingSchedule> Schedules,
    IReadOnlyDictionary<string, List<Guid>> CameraAssignments);

  private Snapshot _snapshot = new(new Dictionary<Guid, RecordingSchedule>(), new Dictionary<string, List<Guid>>());


  public async Task ReloadAsync(CancellationToken ct)
  {
    var schedules = await scheduleDb.ListSchedulesAsync(ct);
    var assignments = await scheduleDb.ListCameraAssignmentsAsync(ct);

    var byId = new Dictionary<Guid, RecordingSchedule>();
    foreach (var s in schedules)
    {
      if (Guid.TryParse(s.Id, out var id))
        byId[id] = s;
    }

    _snapshot = new Snapshot(byId, assignments);
    _reloadedAtUtc = DateTime.UtcNow;
  }

  /// Rebuilds the snapshot when another replica has edited a schedule since the last probe. Cheap
  /// enough for the read path: one state-store get every VersionProbeInterval at most, and a reload
  /// only when the value actually moved.
  public async Task EnsureFreshAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval
      && DateTime.UtcNow - _reloadedAtUtc < CacheTtl)
    {
      return;
    }

    await _refreshLock.WaitAsync(ct);
    try
    {
      var staleByTtl = DateTime.UtcNow - _reloadedAtUtc >= CacheTtl;
      if (DateTime.UtcNow - _versionCheckedAtUtc < VersionProbeInterval && !staleByTtl)
        return;

      string? version = null;
      try
      {
        version = await dapr.GetStateAsync<string>(StateStoreName, VersionKey, cancellationToken: ct);
        _versionCheckedAtUtc = DateTime.UtcNow;
      }
      catch (Exception ex)
      {
        // Leave _versionCheckedAtUtc alone so the next read retries rather than waiting the
        // interval; the TTL below still guarantees the snapshot is rebuilt.
        logger.LogDebug(ex, "Reading the schedule version failed; falling back to the cache TTL");
      }

      var changed = version is not null
        && _lastSeenVersion is not null
        && !string.Equals(version, _lastSeenVersion, StringComparison.Ordinal);
      _lastSeenVersion ??= version;

      if (changed || staleByTtl)
      {
        _lastSeenVersion = version ?? _lastSeenVersion;
        await ReloadAsync(ct);
      }
    }
    finally
    {
      _refreshLock.Release();
    }
  }



  public IReadOnlyList<string> GetCameraScheduleIds(string cameraId)
  {
    var snapshot = _snapshot;
    if (!snapshot.CameraAssignments.TryGetValue(cameraId, out var ids))
      return [];
    return ids.Select(id => id.ToString()).ToList();
  }

  /// True when any window of any schedule assigned to the camera is open.
  public bool IsCameraActive(string cameraId, DateTime utcNow)
  {
    var snapshot = _snapshot;
    if (!snapshot.CameraAssignments.TryGetValue(cameraId, out var ids))
      return false;

    foreach (var id in ids)
    {
      if (snapshot.Schedules.TryGetValue(id, out var schedule)
          && RecordingScheduleEvaluator.IsActive(schedule, utcNow))
        return true;
    }
    return false;
  }

  /// Earliest upcoming state change across every schedule that is actually assigned to a camera.
  public DateTime? NextTransitionUtc(DateTime utcNow)
  {
    var snapshot = _snapshot;
    DateTime? best = null;

    foreach (var id in snapshot.CameraAssignments.Values.SelectMany(v => v).Distinct())
    {
      if (!snapshot.Schedules.TryGetValue(id, out var schedule))
        continue;

      var next = RecordingScheduleEvaluator.NextTransitionUtc(schedule, utcNow);
      if (next is not null && (best is null || next < best))
        best = next;
    }

    return best;
  }
}
