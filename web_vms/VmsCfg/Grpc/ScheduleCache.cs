using Vms.Cfg;

namespace VmsCfg;

/// In-memory snapshot of all recording schedules and their camera assignments.
///
/// ListCameras/GetCamera resolve schedule_active on every read, so that path must not touch the
/// database. Reloaded on startup, after every schedule edit, and whenever the ticker in VmsSingleton
/// asks for the current schedule state.
public sealed class ScheduleCache(ScheduleDb scheduleDb)
{
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
