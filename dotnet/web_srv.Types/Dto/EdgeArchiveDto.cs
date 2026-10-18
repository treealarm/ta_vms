namespace web_srv.Types.Dto;

// Camera-side (SD card) archive, ONVIF Profile G. Timestamps stay ISO 8601 strings all the way to
// the browser: they come from the device, are only ever displayed or echoed back, and round-tripping
// them through DateTime would invent a precision the camera never reported.

public record EdgeCapabilitiesDto
{
  public bool Recording { get; init; }
  public bool Search { get; init; }
  public bool Replay { get; init; }
  /// <summary>Both search and replay — the minimum for browsing and importing.</summary>
  public bool Supported { get; init; }
  public bool DynamicRecordings { get; init; }
  public bool DynamicTracks { get; init; }
  public bool StorageConfiguration { get; init; }
  public bool AuxiliaryCommands { get; init; }
  public uint MaxRecordings { get; init; }
  public IReadOnlyList<string> SupportedAuxiliaryCommands { get; init; } = [];
  /// <summary>Why Profile G is unavailable, when it is. Shown instead of empty panels.</summary>
  public string Message { get; init; } = string.Empty;
}

public record EdgeSummaryDto
{
  public string DataFrom { get; init; } = string.Empty;
  public string DataUntil { get; init; } = string.Empty;
  public uint NumberRecordings { get; init; }
  public bool Supported { get; init; }
  public string Message { get; init; } = string.Empty;  /// <summary>
  /// Camera clock minus ours, in milliseconds, as measured while answering. Null = the camera does
  /// not report its time, and then the timestamps here are the camera's own scale, uncorrected.
  /// </summary>
  public long? ClockOffsetMs { get; init; }
}

public record EdgeTrackDto
{
  public string TrackToken { get; init; } = string.Empty;
  public string TrackType { get; init; } = string.Empty;
  public string Description { get; init; } = string.Empty;
  public string DataFrom { get; init; } = string.Empty;
  public string DataTo { get; init; } = string.Empty;
}

public record EdgeRecordingDto
{
  public string RecordingToken { get; init; } = string.Empty;
  public string SourceName { get; init; } = string.Empty;
  public string SourceDescription { get; init; } = string.Empty;
  public string EarliestRecording { get; init; } = string.Empty;
  public string LatestRecording { get; init; } = string.Empty;
  public string Content { get; init; } = string.Empty;
  public string RecordingStatus { get; init; } = string.Empty;
  public IReadOnlyList<EdgeTrackDto> Tracks { get; init; } = [];
}

public record EdgeRecordingsDto
{
  public IReadOnlyList<EdgeRecordingDto> Recordings { get; init; } = [];
  public string Message { get; init; } = string.Empty;  /// <summary>
  /// Camera clock minus ours, in milliseconds, as measured while answering. Null = the camera does
  /// not report its time, and then the timestamps here are the camera's own scale, uncorrected.
  /// </summary>
  public long? ClockOffsetMs { get; init; }
}

public record EdgeIntervalDto
{
  public string RecordingToken { get; init; } = string.Empty;
  public string TrackToken { get; init; } = string.Empty;
  public string StartTime { get; init; } = string.Empty;
  public string EndTime { get; init; } = string.Empty;
}

public record EdgeIntervalsDto
{
  public IReadOnlyList<EdgeIntervalDto> Intervals { get; init; } = [];
  public bool Searched { get; init; }
  public string Message { get; init; } = string.Empty;  /// <summary>
  /// Camera clock minus ours, in milliseconds, as measured while answering. Null = the camera does
  /// not report its time, and then the timestamps here are the camera's own scale, uncorrected.
  /// </summary>
  public long? ClockOffsetMs { get; init; }
}

public record EdgeRecordingJobDto
{
  public string JobToken { get; init; } = string.Empty;
  public string RecordingToken { get; init; } = string.Empty;
  public string Mode { get; init; } = string.Empty;
  public string SourceToken { get; init; } = string.Empty;
}

public record EdgeRecordingConfigurationDto
{
  public string RecordingToken { get; init; } = string.Empty;
  public string SourceId { get; init; } = string.Empty;
  public string SourceName { get; init; } = string.Empty;
  public string SourceLocation { get; init; } = string.Empty;
  public string SourceDescription { get; init; } = string.Empty;
  public string SourceAddress { get; init; } = string.Empty;
  public string Content { get; init; } = string.Empty;
  /// <summary>ISO 8601 duration; PT0S means unlimited retention on the device.</summary>
  public string MaximumRetentionTime { get; init; } = string.Empty;
}

public record EdgeRecordingJobsDto
{
  public IReadOnlyList<EdgeRecordingJobDto> Jobs { get; init; } = [];
  public IReadOnlyList<EdgeRecordingConfigurationDto> Configurations { get; init; } = [];
  public string Message { get; init; } = string.Empty;
}

public record EdgeStorageConfigurationDto
{
  public string Token { get; init; } = string.Empty;
  public string Type { get; init; } = string.Empty;
  public string LocalPath { get; init; } = string.Empty;
  public string StorageUri { get; init; } = string.Empty;
  public string User { get; init; } = string.Empty;
}

public record EdgeStorageDto
{
  public IReadOnlyList<EdgeStorageConfigurationDto> Configurations { get; init; } = [];
  public bool Supported { get; init; }
  public bool FormatSupported { get; init; }
  /// <summary>The exact vendor auxiliary command a format would send — shown before confirming.</summary>
  public string FormatCommand { get; init; } = string.Empty;
  public string Message { get; init; } = string.Empty;
}

public record EdgeJobModeRequestDto
{
  /// <summary>Active starts the on-camera recording job, Idle stops it.</summary>
  public string Mode { get; init; } = string.Empty;
}

public record EdgeRecordingConfigRequestDto
{
  public string Content { get; init; } = string.Empty;
  public string MaximumRetentionTime { get; init; } = string.Empty;
}

public record EdgeFormatStorageRequestDto
{
  /// <summary>Empty uses the command the camera itself advertises.</summary>
  public string Command { get; init; } = string.Empty;
}

public record EdgeImportRequestDto
{
  public string RecordingToken { get; init; } = string.Empty;
  public string? StreamName { get; init; }
  public string StartTime { get; init; } = string.Empty;
  public string EndTime { get; init; } = string.Empty;
  /// <summary>
  /// Start of the recording the token addresses. Optional — diagnostics only, since the position is
  /// sent to the camera as an absolute clock range rather than as an offset from this.
  /// </summary>
  public string RecordingStartTime { get; init; } = string.Empty;
  /// <summary>Produce a downloadable mp4 instead of writing archive clips.</summary>
  public bool ExportToFile { get; init; }
  public uint SegmentSeconds { get; init; }
}

public record EdgeImportJobDto
{
  public string JobId { get; init; } = string.Empty;
  public string CameraId { get; init; } = string.Empty;
  public string StreamName { get; init; } = string.Empty;
  public string State { get; init; } = string.Empty;
  public DateTime? StartTime { get; init; }
  public DateTime? EndTime { get; init; }
  /// <summary>Wall clock reached so far; the UI shows progress as its position within the window.</summary>
  public DateTime? Position { get; init; }
  public uint ClipsWritten { get; init; }
  public ulong BytesWritten { get; init; }
  public string Message { get; init; } = string.Empty;
  public string ExportPath { get; init; } = string.Empty;
}

public record EdgeExportDto
{
  public string RelPath { get; init; } = string.Empty;
  public string CameraId { get; init; } = string.Empty;
  public ulong SizeBytes { get; init; }
  public DateTime? CreatedAt { get; init; }
}

public record EdgeExportLinkDto
{
  /// <summary>Relative URL carrying a sealed grant; usable directly as a media element source.</summary>
  public string Url { get; init; } = string.Empty;
  public DateTime ExpiresAt { get; init; }
}
