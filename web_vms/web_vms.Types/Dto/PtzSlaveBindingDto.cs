namespace web_vms.Types.Dto;

/// <summary>One marked correspondence between a spot in the master's frame and the slave's aim.</summary>
/// <param name="MasterX">0..1 from the left of the master's frame.</param>
/// <param name="MasterY">0..1 from the top; the spot on the ground, not the middle of an object.</param>
/// <param name="Pan">Where the slave looked, if the camera would say. Its own units, uninterpreted.</param>
/// <param name="PresetToken">The preset standing at the same position, if there is one.</param>
/// <param name="Residual">
/// How far the rest of the survey lands from this point when it is left out of the fit. Null where
/// the survey is too small to check the point at all, or the point has no position to check.
/// Read-only: computed on the way out, ignored on the way in.
/// </param>
public sealed record PtzSlaveBindingPointDto
{
  public float MasterX { get; init; }
  public float MasterY { get; init; }
  public float? Pan { get; init; }
  public float? Tilt { get; init; }
  public string? PresetToken { get; init; }
  public string? PresetName { get; init; }
  public float? Residual { get; init; }
}

/// <summary>How a slave is aimed at the objects its master sees.</summary>
/// <param name="Method">"absolute" (interpolate a position), "presets" (drive to the nearest point's preset) or
/// "geo" (through the map: the master's ground calibration and the slave's landmark calibration; no points).</param>
/// <param name="Unusable">
/// Why this binding cannot aim the camera as it stands, or null when it can. Shown rather than
/// hidden: an operator who has just marked six points needs to know that four of them lack a preset,
/// not to find the switch greyed out.
/// </param>
public sealed record PtzSlaveBindingDto
{
  public string MasterCameraId { get; init; } = string.Empty;
  public string Method { get; init; } = "absolute";
  public DateTime? CalibratedAt { get; init; }
  public IReadOnlyList<PtzSlaveBindingPointDto> Points { get; init; } = [];
  public string? Unusable { get; init; }
}

/// <summary>How much longer to keep a slave from aiming itself. Zero releases it.</summary>
public sealed record PtzSlaveHoldDto(uint Seconds);

/// <summary>A spot in the master's frame to send the slave to, for checking a survey.</summary>
public sealed record PtzSlaveAimDto(float MasterX, float MasterY);

/// <summary>Where a test aim sent the camera.</summary>
public sealed record PtzSlaveAimResultDto
{
  public float? Pan { get; init; }
  public float? Tilt { get; init; }
  public string? PresetToken { get; init; }
  public string? PresetName { get; init; }
  /// <summary>The spot lies outside the marked area, so the mapping is continuing rather than interpolating.</summary>
  public bool Extrapolated { get; init; }
}
