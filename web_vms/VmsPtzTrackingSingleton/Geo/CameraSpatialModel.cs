using Vms.Cfg;

namespace VmsPtzTrackingSingleton.Geo;

/// <summary>A camera where it really stands, in a local frame around itself, with its landmark calibration fitted.</summary>
/// <param name="Space">The space of the camera's reference placement.</param>
/// <param name="HeightAboveGroundM">The space's floor elevation plus the camera's own height.</param>
/// <param name="GeoFit">Null when the observations cannot carry a fit; <see cref="GeoFitError"/> says why.</param>
/// <param name="Zoom">How far to zoom by distance, from the observations that carry a zoom; null when none does.</param>
/// <param name="GroundFit">
/// The picture mapped onto the ground, for a camera marked against landmarks in its picture; null
/// when the marks cannot carry one, and <see cref="GroundFitError"/> says why -- or when there are
/// none at all, and it is null too.
/// </param>
public sealed record CameraSpatialModel(
  string CameraId,
  Space Space,
  EnuFrame Frame,
  Enu Position,
  double HeightAboveGroundM,
  IReadOnlyList<GeoObservationSample> Observations,
  PtzGeoFit? GeoFit,
  string? GeoFitError,
  ZoomByDistance? Zoom,
  IReadOnlyList<GroundSample> GroundSamples,
  GroundHomography? GroundFit,
  string? GroundFitError)
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
        o.Tilt,
        o.HasZoom ? o.Zoom : null))
      .ToList();

    PtzGeoMath.TryFit(observations, out var fit, out var error);
    var zoom = ZoomByDistance.TryBuild(observations
      .Where(o => o.Zoom is not null)
      .Select(o => new ZoomSample(o.Bearing.DistanceM, o.Zoom!.Value)));

    var ground = response.GroundObservations
      .Select(o => new GroundSample(o.LandmarkId, o.X, o.Y, frame.ToEnu(o.Lat, o.Lon), o.FloorElevationM))
      .ToList();
    GroundHomography? groundFit = null;
    string? groundError = null;
    if (ground.Count > 0)
      GroundHomography.TryFit(ground, out groundFit, out groundError);

    return new CameraSpatialModel(
      response.Placement.CameraId, response.Space, frame, position, height, observations, fit, error, zoom,
      ground, groundFit, groundError);
  }

  /// <summary>Whether a point picked in <paramref name="other"/> is somewhere this camera can be aimed.</summary>
  /// <remarks>
  /// The same rule VmsCfg applies to landmarks: one space, or two spaces that both have real
  /// coordinates. A plan in made-up coordinates is a world of its own, whose numbers mean nothing
  /// anywhere else.
  /// </remarks>
  public bool SharesGeometryWith(Space other) =>
    other.Id == Space.Id || (other.RealCoordinates && Space.RealCoordinates);

  /// <summary>Whether this is the same camera, placed and calibrated the same way, as <paramref name="other"/>.</summary>
  /// <remarks>
  /// Compared on what the fits are made from, not on the fits: those are rebuilt on every read and
  /// hold their residuals in dictionaries, which compare by reference. Ordered by landmark, since
  /// VmsCfg does not promise an order.
  /// </remarks>
  public bool SameCalibrationAs(CameraSpatialModel other) =>
    CameraId == other.CameraId
    && Space.Id == other.Space.Id
    && Position == other.Position
    && HeightAboveGroundM == other.HeightAboveGroundM
    && Frame.OriginLat == other.Frame.OriginLat
    && Frame.OriginLon == other.Frame.OriginLon
    && Observations.OrderBy(o => o.LandmarkId, StringComparer.Ordinal)
      .SequenceEqual(other.Observations.OrderBy(o => o.LandmarkId, StringComparer.Ordinal))
    && GroundSamples.OrderBy(s => s.LandmarkId, StringComparer.Ordinal)
      .SequenceEqual(other.GroundSamples.OrderBy(s => s.LandmarkId, StringComparer.Ordinal));

  /// <summary>The zoom to see an object in this direction with, by its distance.</summary>
  /// <param name="extrapolated">
  /// The direction is outside what the landmarks cover, so the aim is a guess continued past them:
  /// the widest sampled zoom, which forgives the most.
  /// </param>
  /// <returns>
  /// 0 -- the wide end, in ONVIF's generic zoom space -- for a camera with no zoom samples: an
  /// object it was not told how to frame is better shown small than cut off.
  /// </returns>
  public float ZoomFor(Bearing bearing, bool extrapolated) =>
    Zoom is null ? 0f : extrapolated ? Zoom.Widest : Zoom.Zoom(bearing.DistanceM);

  /// <summary>Direction and distance from this camera to a point given in WGS84 and height above the ground.</summary>
  public Bearing BearingTo(double lat, double lon, double heightAboveGroundM) =>
    PtzGeoMath.BearingTo(Position, HeightAboveGroundM, Frame.ToEnu(lat, lon), heightAboveGroundM);
}
