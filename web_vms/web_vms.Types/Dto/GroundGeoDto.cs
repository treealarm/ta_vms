namespace web_vms.Types.Dto;

/// <summary>Where a landmark stands on the ground in a fixed camera's picture.</summary>
/// <param name="X">Across the frame, 0..1 from the left — the convention of detection boxes.</param>
/// <param name="Y">Down the frame, 0..1 from the top.</param>
/// <param name="ResidualM">
/// How far the fit of all the marks puts this landmark from where it stands, in metres; null with
/// only four marks. Read-only: computed on the way out, ignored on the way in.
/// </param>
public sealed record GroundObservationDto
{
  public string LandmarkId { get; init; } = string.Empty;
  public float X { get; init; }
  public float Y { get; init; }
  public double? ResidualM { get; init; }
}

/// <summary>A fixed camera's marks and what they add up to.</summary>
/// <param name="Fitted">Whether the marks map the picture onto the ground; null when the fit could not be asked for.</param>
/// <param name="Error">Why there is no mapping, for the administrator.</param>
/// <param name="Matrix">The mapping when there is one; see <see cref="GroundProjectionDto"/>.</param>
public sealed record GroundCalibrationDto
{
  public IReadOnlyList<GroundObservationDto> Observations { get; init; } = [];
  public bool? Fitted { get; init; }
  public string? Error { get; init; }
  public IReadOnlyList<double>? Matrix { get; init; }
}

/// <summary>How to put a camera's detections on the map.</summary>
/// <param name="Matrix">
/// Row-major 3×3 from the normalised frame to WGS84: (x, y, 1) maps to (lat·w, lon·w, w); a point
/// with w ≤ 0 is beyond the horizon.
/// </param>
public sealed record GroundProjectionDto
{
  public string CameraId { get; init; } = string.Empty;
  public IReadOnlyList<double> Matrix { get; init; } = [];
}
