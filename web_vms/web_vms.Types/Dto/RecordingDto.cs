namespace web_vms.Types.Dto
{
  public class RecordingsQueryDto
  {
    public string CameraId { get; set; } = "cam1";
    public string? StreamName { get; set; }
    public DateTime? Timestamp { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public uint? Limit { get; set; } = 50;
  }

  public class RecordingDto
  {
    public string FilePath { get; set; } = default!;
    public string StreamName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public ulong SizeBytes { get; set; }
    public string ClipUid { get; set; } = "";
  }

  public class RecordingsResponseDto
  {
    public List<RecordingDto> Recordings { get; set; } = [];
    public RecordingDto? Nearest { get; set; }
  }

  public class RecordingRangeDto
  {
    public List<RecordingDto> Recordings { get; set; } = [];
  }

  public class RecordingRangesResponseDto
  {
    public List<RecordingRangeDto> Ranges { get; set; } = [];
  }

  public class RetentionEventDto
  {
    public string? EventId { get; set; }
    public string CameraId { get; set; } = default!;
    public DateTime AlarmStartTs { get; set; }
    public DateTime? AlarmEndTs { get; set; }
  }

  public class RetentionEventRowDto
  {
    public string EventId { get; set; } = default!;
    public string CameraId { get; set; } = default!;
    public DateTime AlarmStartTs { get; set; }
    public DateTime? AlarmEndTs { get; set; }
  }

  public class RetentionEventsListDto
  {
    public List<RetentionEventRowDto> Events { get; set; } = [];
    public bool HasMore { get; set; }
    public DateTime? NextBeforeAlarmStart { get; set; }
  }
}
