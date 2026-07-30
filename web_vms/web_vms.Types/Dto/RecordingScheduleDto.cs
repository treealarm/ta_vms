namespace web_vms.Types.Dto;

/// <summary>One recording window. Seconds are counted from local midnight in the schedule's
/// timezone (0..86400); End is exclusive and End &lt;= Start means the window wraps past
/// midnight.</summary>
public record ScheduleWindowDto
{
  /// <summary>"weekly" or "date".</summary>
  public string Kind { get; init; } = "weekly";
  /// <summary>0=Sunday..6=Saturday; used for "weekly" windows.</summary>
  public uint DayOfWeek { get; init; }
  /// <summary>"YYYY-MM-DD"; used for "date" windows.</summary>
  public string Date { get; init; } = string.Empty;
  public uint StartSecond { get; init; }
  public uint EndSecond { get; init; }
}

public record RecordingScheduleDto
{
  public string? Id { get; init; }
  public string Name { get; init; } = string.Empty;
  /// <summary>IANA timezone id, e.g. "Europe/Moscow".</summary>
  public string Timezone { get; init; } = "UTC";
  /// <summary>Seconds before a window start at which recording is switched on, absorbing the
  /// keyframe wait and the notification round-trip.</summary>
  public uint LeadInSeconds { get; init; } = 5;
  public IReadOnlyList<ScheduleWindowDto> Windows { get; init; } = [];
}

public record CameraSchedulesDto
{
  public required IReadOnlyList<string> ScheduleIds { get; init; }
}
