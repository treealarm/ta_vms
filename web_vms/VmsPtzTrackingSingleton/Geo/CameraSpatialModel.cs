using Vms.Cfg;

namespace VmsPtzTrackingSingleton.Geo;

/// <summary>A camera where it really stands, in a local frame around itself, with its landmark calibration fitted.</summary>
/// <param name="Space">The space of the camera's reference placement.</param>
/// <param name="HeightAboveGroundM">The space's floor elevation plus the camera's own height.</param>
/// <param name="GeoFit">Null when the observations cannot carry a fit; <see cref="GeoFitError"/> says why.</param>
public sealed record CameraSpatialModel(
  string CameraId,
  Space Space,
  EnuFrame Frame,
  Enu Position,
  double HeightAboveGroundM,
  IReadOnlyList<GeoObservationSample> Observations,
  PtzGeoFit? GeoFit,
  string? GeoFitError)
{
  /// <summary>Builds the model from what VmsCfg returned for a camera with a reference placement.</summary>
  public static CameraSpatialModel Build(CameraSpatialModelResponse response)
  {
    // Centred on the camera itself: every distance that matters is measured from it, and a frame
    // centred where it is measured is as exact as the flat-earth approximation gets.
    var frame = new EnuFrame(response.Placement.Lat, response.Placement.Lon);
    var position = frame.ToEnu(response.Placement.Lat, response.Placement.Lon);
    var height = response.Space.ElevationM + response.Placement.HeightM;

    var observations = response.Observations
      .Select(o => new GeoObservationSample(
        o.LandmarkId,
        PtzGeoMath.BearingTo(position, height, frame.ToEnu(o.Lat, o.Lon), o.HeightAboveGroundM),
        o.Pan,
        o.Tilt))
      .ToList();

    PtzGeoMath.TryFit(observations, out var fit, out var error);
    return new CameraSpatialModel(
      response.Placement.CameraId, response.Space, frame, position, height, observations, fit, error);
  }

  /// <summary>Whether a point picked in <paramref name="other"/> is somewhere this camera can be aimed.</summary>
  /// <remarks>
  /// The same rule VmsCfg applies to landmarks: one space, or two spaces that both have real
  /// coordinates. A plan in made-up coordinates is a world of its own, whose numbers mean nothing
  /// anywhere else.
  /// </remarks>
  public bool SharesGeometryWith(Space other) =>
    other.Id == Space.Id || (other.RealCoordinates && Space.RealCoordinates);

  /// <summary>Direction and distance from this camera to a point given in WGS84 and height above the ground.</summary>
  public Bearing BearingTo(double lat, double lon, double heightAboveGroundM) =>
    PtzGeoMath.BearingTo(Position, HeightAboveGroundM, Frame.ToEnu(lat, lon), heightAboveGroundM);
}
