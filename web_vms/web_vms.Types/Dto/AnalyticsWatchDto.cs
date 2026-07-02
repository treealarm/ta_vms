namespace web_vms.Types.Dto;

public record AnalyticsWatchDto
{
  public required string CameraId { get; init; }
  public string StreamName { get; init; } = string.Empty;
  public bool Enabled { get; init; }
  /// <summary>"person" / "face" / "vehicle" / "license_plate"</summary>
  public List<string> Classes { get; init; } = [];
  public float MinConfidence { get; init; } = 0.5f;
  public uint SampleFps { get; init; } = 1;
  /// <summary>Read-only; populated from camera_states.armed.</summary>
  public bool Armed { get; init; }
  /// <summary>Debug: video_a attaches a JPEG of each detection to the event stream.</summary>
  public bool AttachDebugCrops { get; init; }
}

public record AnalyticsActivityEntryDto
{
  public required string CameraId { get; init; }
  public long TrackId { get; init; }
  public string Kind { get; init; } = string.Empty;
  public float Confidence { get; init; }
  public float BboxX { get; init; }
  public float BboxY { get; init; }
  public float BboxWidth { get; init; }
  public float BboxHeight { get; init; }
  public DateTime DetectedAt { get; init; }
  public string? PlateText { get; init; }
  /// <summary>Base64 JPEG; null when the watch has no debug crops enabled.</summary>
  public string? CropJpegBase64 { get; init; }
  public bool Persisted { get; init; }
}
