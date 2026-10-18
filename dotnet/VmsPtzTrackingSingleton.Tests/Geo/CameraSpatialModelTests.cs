using Vms.Cfg;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

public class CameraSpatialModelTests
{
  private static Space RealSpace(string id, double elevationM = 0) =>
    new() { Id = id, Name = id, Kind = SpaceKind.Plan, RealCoordinates = true, ElevationM = elevationM };

  private static Space MadeUpSpace(string id) =>
    new() { Id = id, Name = id, Kind = SpaceKind.Plan, RealCoordinates = false };

  private static CameraSpatialModel Model(Space space, double lat, double lon, double heightM) =>
    CameraSpatialModel.Build(new CameraSpatialModelResponse
    {
      Found = true,
      Space = space,
      Placement = new CameraPlacement { CameraId = "cam", SpaceId = space.Id, IsReference = true, Lat = lat, Lon = lon, HeightM = heightM },
    });

  [Fact]
  public void MeasuresFromTheCameraWhereverItStands()
  {
    // Far from (0, 0) on purpose: the frame used to be centred on a stored site origin, and is now
    // centred on the camera, so nothing here may depend on where zero is.
    var model = Model(RealSpace("yard", elevationM: 3), 41.3, 69.24, heightM: 2);
    var oneHundredNorth = 41.3 + 100 / (EnuFrame.EarthRadiusM * Math.PI / 180);

    var bearing = model.BearingTo(oneHundredNorth, 69.24, heightAboveGroundM: 5);

    Assert.Equal(0, model.Position.East, 9);
    Assert.Equal(0, model.Position.North, 9);
    Assert.Equal(5, model.HeightAboveGroundM, 9);
    Assert.Equal(0, bearing.AzimuthDeg, 6);
    Assert.Equal(0, bearing.ElevationDeg, 6);
    Assert.Equal(100, bearing.DistanceM, 3);
  }

  [Fact]
  public void SharesGeometryWithItsOwnSpaceAndOtherRealOnes()
  {
    var real = Model(RealSpace("yard"), 41.3, 69.24, 3);
    var madeUp = Model(MadeUpSpace("office"), 0, 0, 3);

    Assert.True(real.SharesGeometryWith(RealSpace("hangar")));
    Assert.False(real.SharesGeometryWith(MadeUpSpace("office")));
    Assert.True(madeUp.SharesGeometryWith(MadeUpSpace("office")));
    Assert.False(madeUp.SharesGeometryWith(MadeUpSpace("lobby")));
    Assert.False(madeUp.SharesGeometryWith(RealSpace("yard")));
  }

  [Fact]
  public void FitsThePictureToTheGroundOnlyWhenMarked()
  {
    var space = RealSpace("yard");
    var response = new CameraSpatialModelResponse
    {
      Found = true,
      Space = space,
      Placement = new CameraPlacement { CameraId = "cam", SpaceId = space.Id, IsReference = true, Lat = 41.3, Lon = 69.24, HeightM = 6 },
    };
    Assert.Null(CameraSpatialModel.Build(response).GroundFit);
    Assert.Null(CameraSpatialModel.Build(response).GroundFitError);

    // A square of ground seen as a trapezoid, as from above and behind.
    const double m = 1 / (EnuFrame.EarthRadiusM * Math.PI / 180);
    (double X, double Y, double North, double East)[] marks =
      [(0.3, 0.9, 5, -2), (0.7, 0.9, 5, 2), (0.6, 0.4, 15, 2), (0.4, 0.4, 15, -2)];
    foreach (var (x, y, north, east) in marks)
    {
      response.GroundObservations.Add(new GeoGroundObservation
      {
        LandmarkId = $"{x}/{y}", Lat = 41.3 + north * m, Lon = 69.24 + east * m / Math.Cos(41.3 * Math.PI / 180), X = (float)x, Y = (float)y,
      });
    }
    var model = CameraSpatialModel.Build(response);

    Assert.NotNull(model.GroundFit);
    var middle = model.GroundFit.Project(0.5, 0.9);
    Assert.NotNull(middle);
    Assert.Equal(0, middle.Value.East, 3);
    Assert.Equal(5, middle.Value.North, 3);
  }
}
