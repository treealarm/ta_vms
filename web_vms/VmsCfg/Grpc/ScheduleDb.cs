using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

/// Recording schedules and their assignment to cameras. Schedules are pure configuration: nothing
/// here is written at runtime, the active/inactive state is always recomputed from the wall clock.
public class ScheduleDb(IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  public async Task<List<RecordingSchedule>> ListSchedulesAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.RecordingSchedules
      .Include(s => s.Windows)
      .AsNoTracking()
      .OrderBy(s => s.Name)
      .ToListAsync(ct);
    return rows.Select(ToProto).ToList();
  }

  public async Task<Dictionary<string, List<Guid>>> ListCameraAssignmentsAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.CameraSchedules.AsNoTracking().ToListAsync(ct);
    return rows
      .GroupBy(r => r.CameraId)
      .ToDictionary(g => g.Key, g => g.Select(r => r.ScheduleId).ToList());
  }

  /// Inserts or replaces a schedule together with its windows. Returns the schedule id.
  public async Task<Guid> UpsertScheduleAsync(RecordingSchedule schedule, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    var id = Guid.TryParse(schedule.Id, out var parsed) ? parsed : Guid.NewGuid();
    var existing = await db.RecordingSchedules.FirstOrDefaultAsync(s => s.Id == id, ct);

    if (existing is null)
    {
      existing = new DbRecordingSchedule { Id = id };
      db.RecordingSchedules.Add(existing);
    }
    else
    {
      // Windows are replaced wholesale — the editor always submits the complete set.
      await db.RecordingScheduleWindows.Where(w => w.ScheduleId == id).ExecuteDeleteAsync(ct);
    }

    existing.Name          = schedule.Name;
    existing.Timezone      = string.IsNullOrWhiteSpace(schedule.Timezone) ? "UTC" : schedule.Timezone;
    existing.LeadInSeconds = (int)schedule.LeadInSeconds;

    foreach (var w in schedule.Windows)
      db.RecordingScheduleWindows.Add(FromProto(w, id));

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return id;
  }

  public async Task<bool> RemoveScheduleAsync(Guid id, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.RecordingSchedules.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0;
  }

  /// Cameras a schedule is assigned to — used to notify the affected media servers.
  public async Task<List<string>> ListCamerasUsingScheduleAsync(Guid id, CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.CameraSchedules
      .Where(cs => cs.ScheduleId == id)
      .Select(cs => cs.CameraId)
      .ToListAsync(ct);
  }

  public async Task SetCameraSchedulesAsync(
    string cameraId,
    IReadOnlyList<string> scheduleIds,
    CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    await db.CameraSchedules.Where(cs => cs.CameraId == cameraId).ExecuteDeleteAsync(ct);

    foreach (var raw in scheduleIds.Distinct())
    {
      if (!Guid.TryParse(raw, out var scheduleId))
        continue;
      db.CameraSchedules.Add(new DbCameraSchedule { CameraId = cameraId, ScheduleId = scheduleId });
    }

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
  }

  public async Task<List<(string CameraId, string ServerId)>> ListCamerasWithSchedulesAsync(
    CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.CameraSchedules
      .Join(db.CameraSetups, cs => cs.CameraId, c => c.CameraId, (cs, c) => new { c.CameraId, c.ServerId })
      .Distinct()
      .AsNoTracking()
      .ToListAsync(ct);
    return rows.Select(r => (r.CameraId, r.ServerId)).ToList();
  }

  public async Task<Dictionary<string, string>> ListCameraServersAsync(
    IReadOnlyList<string> cameraIds,
    CancellationToken ct)
  {
    if (cameraIds.Count == 0)
      return [];

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.CameraSetups
      .Where(c => cameraIds.Contains(c.CameraId))
      .AsNoTracking()
      .ToDictionaryAsync(c => c.CameraId, c => c.ServerId, ct);
  }

  private static RecordingSchedule ToProto(DbRecordingSchedule s)
  {
    var proto = new RecordingSchedule
    {
      Id            = s.Id.ToString(),
      Name          = s.Name,
      Timezone      = s.Timezone,
      LeadInSeconds = (uint)s.LeadInSeconds,
    };
    foreach (var w in s.Windows.OrderBy(w => w.DateValue).ThenBy(w => w.DayOfWeek).ThenBy(w => w.StartSecond))
    {
      proto.Windows.Add(new ScheduleWindow
      {
        Kind        = w.Kind == "date" ? ScheduleWindowKind.ScheduleWindowDate : ScheduleWindowKind.ScheduleWindowWeekly,
        DayOfWeek   = (uint)(w.DayOfWeek ?? 0),
        Date        = w.DateValue?.ToString("yyyy-MM-dd") ?? string.Empty,
        StartSecond = (uint)w.StartSecond,
        EndSecond   = (uint)w.EndSecond,
      });
    }
    return proto;
  }

  private static DbRecordingScheduleWindow FromProto(ScheduleWindow w, Guid scheduleId)
  {
    var isDate = w.Kind == ScheduleWindowKind.ScheduleWindowDate;
    return new DbRecordingScheduleWindow
    {
      Id          = Guid.NewGuid(),
      ScheduleId  = scheduleId,
      Kind        = isDate ? "date" : "weekly",
      DayOfWeek   = isDate ? null : (short)(w.DayOfWeek % 7),
      DateValue   = isDate && DateOnly.TryParse(w.Date, out var d) ? d : null,
      StartSecond = (int)Math.Min(w.StartSecond, SecondsPerDay - 1),
      EndSecond   = (int)Math.Min(w.EndSecond, SecondsPerDay),
    };
  }

  private const uint SecondsPerDay = 24 * 60 * 60;
}
