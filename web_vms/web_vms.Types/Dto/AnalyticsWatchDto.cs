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
  /// <summary>Persist this camera's detection embeddings for forensic similarity search.</summary>
  public bool StoreEmbeddings { get; init; }
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

public record SearchSimilarDetectionsRequestDto
{
  public DetectionIdProbeDto? Detection { get; init; }
  public ArchiveTrackProbeDto? Archive { get; init; }
  public ImageProbeDto? Image { get; init; }
  public required DateTime FromTs { get; init; }
  public required DateTime ToTs { get; init; }
  public float? MinScore { get; init; }
  public int Limit { get; init; } = 100;
  public List<string> CameraIds { get; init; } = [];
}

public record DetectionIdProbeDto
{
  public long DetectionId { get; init; }
}

public record ArchiveTrackProbeDto
{
  public required string CameraId { get; init; }
  public long TrackId { get; init; }
  public required DateTime Ts { get; init; }
  public string? Kind { get; init; }
}

public record ImageProbeDto
{
  /// <summary>Base64-encoded JPEG bytes.</summary>
  public string JpegBase64 { get; init; } = string.Empty;
  public string Kind { get; init; } = string.Empty;
  public BoundingBoxDto? Bbox { get; init; }
  public bool DetectFirst { get; init; }
}

public record BoundingBoxDto
{
  public float X { get; init; }
  public float Y { get; init; }
  public float Width { get; init; }
  public float Height { get; init; }
}

public record SimilarDetectionHitDto
{
  public required AnalyticsDetectionDto Detection { get; init; }
  public float Score { get; init; }
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
  /// <summary>
  /// The producer's own word for the class when it says more than <see cref="Kind"/> does —
  /// "Truck" under vehicle, "Bag" under unknown. ONVIF lets a camera report free-form classes
  /// alongside its five-value enum; folding those into the enum would throw the detail away.
  /// </summary>
  public string? Label { get; init; }
}

/// <summary>
/// An ONVIF event on its way to the live monitor. Carries no geometry: cameras whose analytics is
/// cell motion or tamper have nothing to draw, and the event is the whole statement they can make.
/// </summary>
public record LiveCameraEventDto
{
  public required string CameraId { get; init; }
  /// <summary>Full ONVIF topic, matching what the alarm filters are written against.</summary>
  public string Topic { get; init; } = string.Empty;
  /// <summary>Short name from the last topic segment ("Motion", "Tamper").</summary>
  public string Label { get; init; } = string.Empty;
  /// <summary>True when the condition started, false when it cleared.</summary>
  public bool Active { get; init; }
  /// <summary>
  /// True when the event matched one of the camera's alarm filters. Subscribing is what puts an
  /// event on this stream; matching is only what makes it worth keeping a clip for, so the monitor
  /// needs both facts to decide what to show.
  /// </summary>
  public bool Matched { get; init; }
  public DateTime ReceivedAt { get; init; }
}
