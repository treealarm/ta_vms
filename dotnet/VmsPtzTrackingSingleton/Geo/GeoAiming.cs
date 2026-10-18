namespace VmsPtzTrackingSingleton.Geo;

/// <summary>A master and a slave, each with its calibration, ready to aim one at what the other sees.</summary>
public sealed record GeoAimPair(CameraSpatialModel Master, CameraSpatialModel Slave);

/// <summary>Where a spot in the master's picture is on the ground, and where the slave points to see it.</summary>
/// <param name="Ground">On the ground, in the master's local frame.</param>
public sealed record GeoAimProjection(Enu Ground, double Lat, double Lon, Bearing Bearing, GeoAim Aim);

/// <summary>
/// Aims a slave through the map rather than through a survey: the master's ground calibration
/// turns a spot in its picture into a place, and the slave's landmark calibration turns that place
/// into a pan and tilt.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is marked against the pair. Each camera is calibrated on its own against landmarks, so a
/// pair works the moment both are, and a camera recalibrated alone stays right for every master or
/// slave it is paired with -- which a survey of the pair cannot do.
/// </para>
/// <para>
/// The spot is the foot of an object, as for a survey: the master's homography knows only its
/// floor, and that floor is where the slave is sent. The slave therefore looks at feet, a few
/// degrees low of a person's middle. Zoomed, that is what the zoom samples allow for: the operator
/// records them with a landmark in the crosshair, so a frame chosen to show a person standing there
/// is chosen with their feet in its middle -- as the slave will hold them.
/// </para>
/// </remarks>
public static class GeoAiming
{
  /// <summary>What stops the pair from aiming, in the order an administrator would fix it.</summary>
  /// <returns>Null when nothing does.</returns>
  public static string? Unusable(
    string masterCameraId, CameraSpatialModel? master, CameraSpatialModel? slave)
  {
    if (slave is null)
      return "The camera has no reference placement: mark where it really stands first";
    if (master is null)
      return $"Master '{masterCameraId}' has no reference placement: mark where it really stands first";
    if (!master.SharesGeometryWith(slave.Space))
      return $"The master stands in '{master.Space.Name}', which shares no coordinates with '{slave.Space.Name}'";
    if (master.GroundFit is null)
      return master.GroundFitError ?? $"Master '{masterCameraId}' is not marked against landmarks in its picture";
    if (slave.GeoFit is null)
      return slave.GeoFitError ?? "The camera is not calibrated against landmarks";
    return null;
  }

  /// <param name="x">Across the master's frame, 0..1 from the left.</param>
  /// <param name="y">Down the master's frame, 0..1 from the top.</param>
  /// <returns>Null when the spot is beyond the master's horizon, or either calibration is missing.</returns>
  public static GeoAimProjection? Project(GeoAimPair pair, double x, double y)
  {
    var (master, slave) = pair;
    if (master.GroundFit is not { } ground || slave.GeoFit is not { } geo)
      return null;
    if (ground.Project(x, y) is not { } spot)
      return null;

    // Through WGS84 because the two cameras' local frames are centred on themselves: the only
    // coordinates they share are the map's.
    var (lat, lon) = master.Frame.FromEnu(spot);
    var bearing = slave.BearingTo(lat, lon, ground.FloorElevationM);
    return new GeoAimProjection(spot, lat, lon, bearing, PtzGeoMath.Project(geo, bearing));
  }
}
