using System.Text.Json.Serialization;

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

  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum RecordingCoverageGranularityDto
  {
    Year,
    Month,
    Day,
  }

  public class RecordingCoverageQueryDto
  {
    public string CameraId { get; set; } = "cam1";
    public RecordingCoverageGranularityDto Granularity { get; set; } = RecordingCoverageGranularityDto.Day;
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
  }

  public class RecordingCoverageResponseDto
  {
    // "YYYY", "YYYY-MM" or "YYYY-MM-DD" (UTC), matching the requested granularity.
    public List<string> Buckets { get; set; } = [];
  }

  public class RetentionEventDto
  {
    public string? EventId { get; set; }
    public string CameraId { get; set; } = default!;
    public string? StreamName { get; set; }
    public DateTime AlarmStartTs { get; set; }
    public DateTime? AlarmEndTs { get; set; }
    public uint TimeoutSeconds { get; set; }
  }

  public class RetentionEventRowDto
  {
    public string EventId { get; set; } = default!;
    public string CameraId { get; set; } = default!;
    public string? StreamName { get; set; }
    public DateTime AlarmStartTs { get; set; }
    public DateTime? AlarmEndTs { get; set; }
  }

  public class RetentionEventsListDto
  {
    public List<RetentionEventRowDto> Events { get; set; } = [];
    public bool HasMore { get; set; }
    public DateTime? NextBeforeAlarmStart { get; set; }
  }

  public class EventClipsResponseDto
  {
    public List<RecordingDto> Clips { get; set; } = [];
  }

  public class FsFileEventDto
  {
    public string   ClipUid    { get; init; } = "";
    public string   FilePath   { get; init; } = "";
    public string   CameraId   { get; init; } = "";
    public string   StreamName { get; init; } = "";
    public bool     Retained   { get; init; }
    public long     SizeBytes  { get; init; }
    public DateTime OccurredAt { get; init; }
    public string   EventType  { get; init; } = "";
    public DateTime? EndTs     { get; init; }
  }

  public class FsActivityResponseDto
  {
    public List<FsFileEventDto> Events { get; set; } = [];
  }
}
