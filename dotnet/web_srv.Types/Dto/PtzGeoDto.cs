namespace web_srv.Types.Dto;

/// <summary>Where a PTZ camera pointed when centred on a landmark.</summary>
/// <param name="Pan">The camera's own units, as GET /api/ptz/status reported them.</param>
/// <param name="PanResidual">
/// How far the other observations put this landmark when it is left out of the fit. Null where the
/// rest cannot be fitted without it. Read-only: computed on the way out, ignored on the way in.
/// </param>
/// <param name="Zoom">
/// The zoom a slave uses for an object at this landmark's distance, in the camera's own units; null
/// when none was recorded.
/// </param>
/// <param name="DistanceM">From the camera to the landmark. Read-only, like the residuals.</param>
public sealed record PtzGeoObservationDto
{
  public string LandmarkId { get; init; } = string.Empty;
  public float Pan { get; init; }
  public float Tilt { get; init; }
  public float? Zoom { get; init; }
  public float? PanResidual { get; init; }
  public float? TiltResidual { get; init; }
  public double? DistanceM { get; init; }
}

/// <summary>A camera's landmark calibration and what it adds up to.</summary>
/// <param name="Fitted">Whether the observations carry a mapping; null when the fit could not be asked for.</param>
/// <param name="Error">Why there is no mapping, for the operator.</param>
public sealed record PtzGeoCalibrationDto
{
  public IReadOnlyList<PtzGeoObservationDto> Observations { get; init; } = [];
  public bool? Fitted { get; init; }
  public string? Error { get; init; }
}

/// <param name="SpaceId">Where the point was picked; must share coordinates with the camera's own space.</param>
/// <param name="HeightM">Above that space's floor.</param>
public sealed record PtzGeoAimDto
{
  public string SpaceId { get; init; } = string.Empty;
  public double Lat { get; init; }
  public double Lon { get; init; }
  public double HeightM { get; init; }
}

public sealed record PtzGeoAimResultDto
{
  public float Pan { get; init; }
  public float Tilt { get; init; }
  public double AzimuthDeg { get; init; }
  public double ElevationDeg { get; init; }
  public double DistanceM { get; init; }
  public bool Extrapolated { get; init; }
  /// <summary>From the camera's zoom samples by that distance; 0, the wide end, without any.</summary>
  public float Zoom { get; init; }
}
