namespace VmsPtzTrackingSingleton;

/// <summary>Maps image-space tracking error to normalized pan/tilt commands.</summary>
public static class PtzTrackingMath
{
  private const float MinDeterminant = 0.05f;

  public static (float Pan, float Tilt) ImageErrorToCommand(
    float errorX,
    float errorY,
    PtzCalibrationModel calibration)
  {
    var det = (calibration.PanDx * calibration.TiltDy) - (calibration.PanDy * calibration.TiltDx);
    if (MathF.Abs(det) < MinDeterminant)
      return (errorX, -errorY);

    var pan = ((-errorX * calibration.TiltDy) + (errorY * calibration.TiltDx)) / det;
    var tilt = ((-calibration.PanDx * errorY) + (calibration.PanDy * errorX)) / det;
    return (pan, tilt);
  }
}
