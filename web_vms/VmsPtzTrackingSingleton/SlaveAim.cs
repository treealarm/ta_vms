using Vms.Cfg;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton;

/// <summary>A position to send the slave to, and what it was computed for (for the log).</summary>
/// <param name="Zoom">In the camera's own zoom units; 0, the wide end, unless the map method has zoom samples.</param>
/// <param name="Ground">
/// On the map method, where the object stands, in the master's local frame: what the dead zone
/// measures, because there it means the same at any zoom. Null on a survey.
/// </param>
public sealed record PositionAim(float Pan, float Tilt, float Zoom, Enu? Ground, bool Extrapolated, string Describe);

/// <summary>
/// Everything a slave is aimed by: its binding and, on the map method, both cameras' calibrations.
/// </summary>
/// <remarks>
/// <para>
/// One object, so that the two questions every caller asks -- can this aim at all, and where does a
/// spot in the master's frame send the camera -- are answered in one place whatever the method,
/// rather than by a switch in every caller. The manager builds it (it owns the caches the map
/// method needs); the session, the test aim and the binding's status all use it as it is.
/// </para>
/// <para>
/// Replaced whole, never patched: a session swapping in a new binding and the calibrations that go
/// with it in two steps could aim one detection by the new master's picture through the old
/// master's ground calibration.
/// </para>
/// </remarks>
/// <param name="Geo">On the map method, both cameras' models; null otherwise, or when they cannot aim.</param>
/// <param name="GeoProblem">On the map method, why the calibrations cannot aim; null when they can.</param>
public sealed record SlaveAim(SlaveBindingModel Binding, GeoAimPair? Geo, string? GeoProblem)
{
  public string SlaveCameraId => Binding.SlaveCameraId;
  public string MasterCameraId => Binding.MasterCameraId;

  /// <summary>Whether the camera can be aimed by the preset of the nearest marked point.</summary>
  public bool ByPreset => Binding.Method == PtzSlaveBindingMethod.PtzSlaveBindingPresets;

  /// <summary>A binding on a method that needs nothing beyond the binding itself.</summary>
  public static SlaveAim BySurvey(SlaveBindingModel binding) => new(binding, null, null);

  /// <returns>Null when this can aim the camera, otherwise what is missing.</returns>
  public string? Unusable() =>
    Binding.Method == PtzSlaveBindingMethod.PtzSlaveBindingGeo
      ? Geo is null ? GeoProblem ?? "The calibrations of the master and this camera are not loaded" : null
      : Binding.SurveyUnusable();

  /// <summary>Where a spot in the master's frame sends the camera, on a method that commands a position.</summary>
  /// <param name="x">Across the master's frame, 0..1 from the left.</param>
  /// <param name="y">Down the master's frame, 0..1 from the top: the foot of the object.</param>
  /// <returns>The position, or why there is none for that spot.</returns>
  public (PositionAim? Aim, string? Error) Position(float x, float y)
  {
    if (Unusable() is { } unusable)
      return (null, unusable);
    if (ByPreset)
      return (null, "This camera is aimed by presets, not by position");

    if (Geo is { } geo)
    {
      var projection = GeoAiming.Project(geo, x, y);
      if (projection is null)
        return (null, "The spot is beyond the master's horizon, where its ground calibration says nothing");
      var aim = projection.Aim;
      var zoom = geo.Slave.ZoomFor(projection.Bearing, aim.Extrapolated);
      return (new PositionAim(aim.Pan, aim.Tilt, zoom, projection.Ground, aim.Extrapolated,
        $"master point ({x:F3},{y:F3}) — {projection.Lat:F6},{projection.Lon:F6}, "
        + $"{projection.Bearing.DistanceM:F0} m at {projection.Bearing.AzimuthDeg:F1}°"
        + (aim.Extrapolated ? ", outside the landmarks' sector" : string.Empty)
        + $", zoom {zoom:F3}"), null);
    }

    var surveyed = PtzSlaveBindingMath.Project(Binding.Fit!, x, y);
    return (new PositionAim(surveyed.Pan, surveyed.Tilt, 0f, null, surveyed.Extrapolated,
      $"master point ({x:F3},{y:F3}){(surveyed.Extrapolated ? " — outside the marked area" : string.Empty)}"), null);
  }
}
