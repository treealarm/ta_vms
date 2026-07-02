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
}
