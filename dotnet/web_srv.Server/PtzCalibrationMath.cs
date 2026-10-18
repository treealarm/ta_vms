namespace web_srv;

/// <summary>Turns a calibration's test-move observations into the image vectors it stores.</summary>
public static class PtzCalibrationMath
{
  private const float MinVectorLength = 0.02f;
  private const float MaxParallelCosine = 0.94f;

  public static (float Dx, float Dy) DirectionToVector(string direction)
  {
    return direction.Trim().ToLowerInvariant() switch
    {
      "left" => (-1f, 0f),
      "right" => (1f, 0f),
      // Image y grows downward; "up" is the picture moving toward the top of the frame.
      "up" => (0f, -1f),
      "down" => (0f, 1f),
      // Clockwise / counterclockwise are not translations. Encoding them as diagonals inverted
      // the tilt axis. A pan/tilt pulse moves the scene; the operator names that translation.
      _ => throw new ArgumentException($"Unknown direction '{direction}'"),
    };
  }

  /// <summary>
  /// The measured shift is too small to carry a direction.
  /// </summary>
  /// <remarks>
  /// Must be asked of the raw motion estimate, in fractions of the frame, and never of a
  /// normalized vector — normalizing turns a one-pixel wobble into a full-length arrow pointing
  /// wherever the noise happened to point, which is exactly how a camera that did not tilt used to
  /// be reported as one whose axes "look too similar".
  /// </remarks>
  public static bool IsTooSmall(float dx, float dy) =>
    MathF.Sqrt((dx * dx) + (dy * dy)) < MinVectorLength;

  public static bool IsDegenerate(float panDx, float panDy, float tiltDx, float tiltDy)
  {
    var panLen = MathF.Sqrt((panDx * panDx) + (panDy * panDy));
    var tiltLen = MathF.Sqrt((tiltDx * tiltDx) + (tiltDy * tiltDy));
    if (panLen < MinVectorLength || tiltLen < MinVectorLength)
      return true;

    var cosine = MathF.Abs(((panDx * tiltDx) + (panDy * tiltDy)) / (panLen * tiltLen));
    return cosine >= MaxParallelCosine;
  }

  public static (float Dx, float Dy) NormalizeVector(float dx, float dy)
  {
    var len = MathF.Sqrt((dx * dx) + (dy * dy));
    return len < 1e-6f ? (dx, dy) : (dx / len, dy / len);
  }
}
