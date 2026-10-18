using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsCfg;

/// Pure wall-clock evaluation of recording schedules. Nothing here is stateful: both "is a window
/// open right now" and "when does the state change next" are functions of (schedule, utcNow), which
/// is what makes schedules survive a restart of any service without stored state.
public static class RecordingScheduleEvaluator
{
  /// How far ahead NextTransitionUtc looks. Weekly windows repeat, so a boundary is always found
  /// within a week; the extra day covers windows that wrap past midnight.
  private static readonly int LookaheadDays = 8;

  private static readonly ConcurrentDictionary<string, TimeZoneInfo> TimeZones = new();

  private const int SecondsPerDay = 24 * 60 * 60;

  /// True while any window of the schedule is open. A window opens lead_in_seconds before its
  /// start_second (absorbing the keyframe wait and the notification round-trip) and closes exactly
  /// at end_second.
  public static bool IsActive(RecordingSchedule schedule, DateTime utcNow)
  {
    if (schedule.Windows.Count == 0)
      return false;

    var tz = ResolveTimeZone(schedule.Timezone);
    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);

    // The lead-in is applied by asking whether a window would be open lead_in_seconds from now,
    // rather than by moving window starts back: that keeps the closing edge exactly at end_second.
    return Contains(schedule, local)
        || Contains(schedule, local.AddSeconds(schedule.LeadInSeconds));
  }

  /// The next instant at which IsActive flips, or null if the schedule has no upcoming boundary.
  /// Used only to decide how long to sleep — IsActive remains the authority.
  public static DateTime? NextTransitionUtc(RecordingSchedule schedule, DateTime utcNow)
  {
    if (schedule.Windows.Count == 0)
      return null;

    var tz = ResolveTimeZone(schedule.Timezone);
    var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz);
    var lead = TimeSpan.FromSeconds(schedule.LeadInSeconds);

    DateTime? best = null;
    // Start a day early so a window that wrapped past midnight is still considered.
    for (var offset = -1; offset <= LookaheadDays; offset++)
    {
      var day = local.Date.AddDays(offset);
      foreach (var window in schedule.Windows)
      {
        if (!MatchesDay(window, day))
          continue;

        var wraps = window.EndSecond <= window.StartSecond;
        var startLocal = day.AddSeconds(window.StartSecond) - lead;
        var endLocal = wraps
          ? day.AddDays(1).AddSeconds(window.EndSecond)
          : day.AddSeconds(window.EndSecond);

        foreach (var boundaryLocal in new[] { startLocal, endLocal })
        {
          var boundaryUtc = ToUtc(boundaryLocal, tz);
          if (boundaryUtc is null || boundaryUtc <= utcNow)
            continue;
          if (best is null || boundaryUtc < best)
            best = boundaryUtc;
        }
      }
    }

    return best;
  }

  private static bool Contains(RecordingSchedule schedule, DateTime local)
  {
    // Fractional seconds so a boundary lands on the exact instant, not the enclosing second.
    var seconds = local.TimeOfDay.TotalSeconds;

    foreach (var window in schedule.Windows)
    {
      var wraps = window.EndSecond <= window.StartSecond;

      if (MatchesDay(window, local.Date)
          && seconds >= window.StartSecond
          && seconds < (wraps ? SecondsPerDay : window.EndSecond))
        return true;

      // Tail of a window that started the previous day and wrapped past midnight.
      if (wraps && MatchesDay(window, local.Date.AddDays(-1)) && seconds < window.EndSecond)
        return true;
    }

    return false;
  }

  private static bool MatchesDay(ScheduleWindow window, DateTime localDate) =>
    window.Kind == ScheduleWindowKind.ScheduleWindowDate
      ? DateOnly.TryParse(window.Date, out var d) && d == DateOnly.FromDateTime(localDate)
      : (uint)localDate.DayOfWeek == window.DayOfWeek % 7;

  /// Local wall clock to UTC. Returns null for a local time that does not exist (the DST
  /// spring-forward gap) after one attempt to skip the gap; boundary math is best-effort, the 60 s
  /// sleep ceiling in RecordingScheduleService covers any miss.
  private static DateTime? ToUtc(DateTime local, TimeZoneInfo tz)
  {
    var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
    if (tz.IsInvalidTime(unspecified))
      unspecified = unspecified.AddHours(1);
    if (tz.IsInvalidTime(unspecified))
      return null;
    return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
  }

  /// Whether the id names a timezone this machine knows. Upserts are validated with this so a typo
  /// is refused at the API instead of silently shifting every window of the schedule.
  public static bool IsKnownTimeZone(string id)
  {
    if (string.IsNullOrWhiteSpace(id))
      return false;
    try
    {
      TimeZoneInfo.FindSystemTimeZoneById(id);
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }

  private static TimeZoneInfo ResolveTimeZone(string id)
  {
    if (string.IsNullOrWhiteSpace(id))
      return TimeZoneInfo.Utc;

    return TimeZones.GetOrAdd(id, key =>
    {
      try
      {
        return TimeZoneInfo.FindSystemTimeZoneById(key);
      }
      catch (Exception ex)
      {
        // Unreachable for schedules written through CfgService, which rejects an unknown id up
        // front. This only covers a row that predates that validation or was edited in the
        // database by hand: keep evaluating in UTC rather than crash the ticker, and shout about it.
        Console.Error.WriteLine(
          $"VmsCfg: schedule timezone '{key}' is unknown to this machine, evaluating it as UTC — " +
          $"fix the schedule's timezone: {ex.Message}");
        return TimeZoneInfo.Utc;
      }
    });
  }
}
