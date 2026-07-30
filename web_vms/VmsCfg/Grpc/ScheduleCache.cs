using Vms.Cfg;

namespace VmsCfg;

/// In-memory snapshot of all recording schedules and their camera assignments.
///
/// ListCameras/GetCamera resolve schedule_active on every read, so that path must not touch the
/// database. The snapshot is reloaded on startup, after every schedule edit, and on each tick of
/// RecordingScheduleService.
public sealed class ScheduleCache(ScheduleDb scheduleDb)
{
  private sealed record Snapshot(
    IReadOnlyDictionary<Guid, RecordingSchedule> Schedules,
    IReadOnlyDictionary<string, List<Guid>> CameraAssignments);

  private Snapshot _snapshot = new(new Dictionary<Guid, RecordingSchedule>(), new Dictionary<string, List<Guid>>());

  // Released whenever schedules change, so the ticker can recompute its next boundary immediately
  // instead of sleeping on a stale one.
  private readonly SemaphoreSlim _changed = new(0, 1);

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
  }

  /// Reload after an edit and wake the ticker.
  public async Task ReloadAndSignalAsync(CancellationToken ct)
  {
    await ReloadAsync(ct);

    // A coalescing signal: one pending wake-up is enough, and two concurrent edits must not throw.
    // Checking CurrentCount first would be a check-then-act race — both callers could see 0 and the
    // second Release would blow up inside an rpc handler whose DB write has already committed.
    try
    {
      _changed.Release();
    }
    catch (SemaphoreFullException)
    {
      // The ticker has not consumed the previous signal yet, which is exactly what we wanted.
    }
  }

  /// Sleeps for at most <paramref name="timeout"/>, returning early when schedules change.
  public async Task WaitForChangeAsync(TimeSpan timeout, CancellationToken ct)
  {
    try
    {
      await _changed.WaitAsync(timeout, ct);
    }
    catch (OperationCanceledException)
    {
      // Shutdown — the caller's loop checks the token.
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
