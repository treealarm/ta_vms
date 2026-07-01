namespace web_vms.Types.Dto;

public record AnalyticsWatchCameraLinkDto
{
  public required string CameraId { get; init; }
  /// <summary>Ignored on write; populated from camera_states.armed on read.</summary>
  public bool Armed { get; init; }
}

public record AnalyticsWatchDto
{
  public string Id { get; init; } = string.Empty;
  public string RtspUrl { get; init; } = string.Empty;
  public string? CredUser { get; init; }
  public string? CredPass { get; init; }
  public bool Enabled { get; init; }
  /// <summary>"person" / "face" / "vehicle" / "license_plate"</summary>
  public List<string> Classes { get; init; } = [];
  public float MinConfidence { get; init; } = 0.5f;
  public uint SampleFps { get; init; } = 1;
  public List<AnalyticsWatchCameraLinkDto> Cameras { get; init; } = [];
}
