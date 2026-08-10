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
  /// <summary>Seconds of recording kept after the last detection before the event closes.</summary>
  public uint TimeoutSeconds { get; init; } = 10;
}

public record AnalyticsDetectionDto
{
  public long Id { get; init; }
  public required string EventId { get; init; }
  public string EventUuid { get; init; } = string.Empty;
  public string? ObjectId { get; init; }
  public required string CameraId { get; init; }
  public long TrackId { get; init; }
  public string Kind { get; init; } = string.Empty;
  public float Confidence { get; init; }
  public DateTime DetectedAt { get; init; }
  public string? PlateText { get; init; }
  public bool HasCrop { get; init; }
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
  /// <summary>Re-identification label of the physical object, when resolved for this track.</summary>
  public string? ObjectId { get; init; }
  /// <summary>
  /// Where the box came from: "cv" (the video_a detector) or "onvif" (the camera's own Scene
  /// Description). The two number their tracks independently, so the overlay has to key on this as
  /// well or a camera running both would have them overwrite each other.
  /// </summary>
  public string Source { get; init; } = "cv";
}
