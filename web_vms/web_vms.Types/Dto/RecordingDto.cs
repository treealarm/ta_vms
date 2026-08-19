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
    /// <summary>"live", or "edge" for a range recovered from the camera's own storage.</summary>
    public string Origin { get; set; } = "live";

    /// <summary>
    /// Whether this clip has been re-encoded with ROI — a second lossy pass, so worth telling
    /// an operator about for the same reason <see cref="Origin"/> is.
    /// </summary>
    public bool RoiTranscoded { get; set; }
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

  public class RecordingTimelineQueryDto
  {
    public List<string> CameraIds { get; set; } = [];
    public string? StreamName { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    /// Gaps at or below this are drawn as continuous. Callers pass the wall time of one screen
    /// pixel so the response stays small no matter how far the timeline is zoomed out.
    public int MinGapMs { get; set; }
  }

  public class RecordingIntervalDto
  {
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    /// <summary>"live", or "edge" for footage recovered from the camera's own storage.</summary>
    public string Origin { get; set; } = "live";
    /// <summary>Whether this stretch has been re-encoded with ROI.</summary>
    public bool RoiTranscoded { get; set; }
  }

  public class RecordingTimelineTrackDto
  {
    public string CameraId { get; set; } = default!;
    public List<RecordingIntervalDto> Intervals { get; set; } = [];
  }

  public class RecordingTimelineResponseDto
  {
    public List<RecordingTimelineTrackDto> Tracks { get; set; } = [];
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
    /// <summary>
    /// What set the alarm off, in the producer's own words — the ONVIF topic for an ONVIF alarm.
    /// Null for producers that name no cause.
    /// </summary>
    public string? Trigger { get; set; }
  }

  public class RetentionEventsListDto
  {
    public List<RetentionEventRowDto> Events { get; set; } = [];
    public bool HasMore { get; set; }
    public DateTime? NextBeforeAlarmStart { get; set; }
  }

  // One episode = one retention open->close cycle (one retention_events row). The user selects an
  // episode; archive/details load by its EventUuid.
  public class EpisodeDto
  {
    public string EventUuid { get; set; } = default!;
    public string EventId { get; set; } = default!;
    public string? ObjectId { get; set; }
    public string CameraId { get; set; } = default!;
    public string? StreamName { get; set; }
    public DateTime AlarmStartTs { get; set; }
    public DateTime? AlarmEndTs { get; set; }
    /// <summary>What set the alarm off — the ONVIF topic for an ONVIF alarm; null when unnamed.</summary>
    public string? Trigger { get; set; }
  }

  // A group = all episodes sharing one EventId (a time-clustered session of one object, possibly
  // across cameras). Rendered as a tree row with a "+N" badge; Episodes are ordered oldest-first.
  public class EventGroupDto
  {
    public string EventId { get; set; } = default!;
    public string? ObjectId { get; set; }
    public string RepresentativeCameraId { get; set; } = default!;
    public DateTime EarliestAlarmStart { get; set; }
    public DateTime LatestAlarmStart { get; set; }
    public List<EpisodeDto> Episodes { get; set; } = [];
  }

  public class RetentionEventGroupsListDto
  {
    public List<EventGroupDto> Groups { get; set; } = [];
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
