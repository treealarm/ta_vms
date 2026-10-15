using Vms.Cfg;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

/// <summary>
/// A fixed master and a PTZ slave over flat ground, each calibrated against the same five landmarks
/// the way the wizards would record them: the master's marks are where an ideal pinhole puts the
/// landmarks' feet, the slave's readings are where a level head with a linear generic space points.
/// The same arrangement as tests/onvifsim/world.py, so a failure here and there point the same way.
/// </summary>
internal static class TwoCameraWorld
{
  public const double Lat0 = 41.3;
  public const double Lon0 = 69.24;

  // Metres east and north of the origin, height above the ground.
  public static readonly (double East, double North, double Height) Master = (0, -20, 6);
  public const double MasterHeading = 0, MasterPitch = -18, MasterHfov = 70, Aspect = 16.0 / 9.0;

  public static readonly (double East, double North, double Height) Slave = (28, 14, 9);
  public const double SlaveHeading = 240;
  public const double TiltMin = -90, TiltMax = 20;

  public static readonly (string Id, double East, double North)[] Landmarks =
    [("L1", -9, -4), ("L2", 9, -4), ("L3", -13, 12), ("L4", 13, 12), ("L5", 0, 24)];

  private static readonly double MetresPerDegLat = EnuFrame.EarthRadiusM * Math.PI / 180;
  private static readonly double MetresPerDegLon = MetresPerDegLat * Math.Cos(Lat0 * Math.PI / 180);

  public static (double Lat, double Lon) Wgs84(double east, double north) =>
    (Lat0 + north / MetresPerDegLat, Lon0 + east / MetresPerDegLon);

  /// <summary>Where a ground point lands in the master's picture, 0..1 from the top-left.</summary>
  public static (double X, double Y) InMaster(double east, double north, double height = 0)
  {
    var (h, p) = (MasterHeading * Math.PI / 180, MasterPitch * Math.PI / 180);
    // East, north, up.
    var fwd = (Math.Sin(h) * Math.Cos(p), Math.Cos(h) * Math.Cos(p), Math.Sin(p));
    var right = (Math.Cos(h), -Math.Sin(h), 0.0);
    var up = Cross(right, fwd);
    var d = (east - Master.East, north - Master.North, height - Master.Height);
    var depth = Dot(d, fwd);
    var tanH = Math.Tan(MasterHfov / 2 * Math.PI / 180);
    var xNdc = Dot(d, right) / depth / tanH;
    var yNdc = Dot(d, up) / depth / (tanH / Aspect);
    return ((xNdc + 1) / 2, (1 - yNdc) / 2);
  }

  /// <summary>The slave's generic pan and tilt when centred on a ground point.</summary>
  public static (double Pan, double Tilt) SlaveReading(double east, double north, double height = 0)
  {
    var (de, dn, du) = (east - Slave.East, north - Slave.North, height - Slave.Height);
    var azimuth = Math.Atan2(de, dn) * 180 / Math.PI;
    var elevation = Math.Atan2(du, Math.Sqrt(de * de + dn * dn)) * 180 / Math.PI;
    var pan = ((azimuth - SlaveHeading) % 360 + 540) % 360 - 180;
    return (pan / 180, (elevation - TiltMin) / (TiltMax - TiltMin) * 2 - 1);
  }

  public static Space Map(string id = "lab", bool real = true) =>
    new() { Id = id, Name = id, Kind = SpaceKind.Map, RealCoordinates = real };

  public static CameraSpatialModel MasterModel(Space? space = null, bool marked = true)
  {
    space ??= Map();
    var (lat, lon) = Wgs84(Master.East, Master.North);
    var response = new CameraSpatialModelResponse
    {
      Found = true,
      Space = space,
      Placement = new CameraPlacement { CameraId = "master", SpaceId = space.Id, IsReference = true, Lat = lat, Lon = lon, HeightM = Master.Height },
    };
    if (marked)
    {
      foreach (var (id, east, north) in Landmarks)
      {
        var (x, y) = InMaster(east, north);
        var (mlat, mlon) = Wgs84(east, north);
        response.GroundObservations.Add(new GeoGroundObservation
        {
          LandmarkId = id, LandmarkName = id, Lat = mlat, Lon = mlon, X = (float)x, Y = (float)y,
        });
      }
    }
    return CameraSpatialModel.Build(response);
  }

  public static CameraSpatialModel SlaveModel(Space? space = null, bool calibrated = true)
  {
    space ??= Map();
    var (lat, lon) = Wgs84(Slave.East, Slave.North);
    var response = new CameraSpatialModelResponse
    {
      Found = true,
      Space = space,
      Placement = new CameraPlacement { CameraId = "slave", SpaceId = space.Id, IsReference = true, Lat = lat, Lon = lon, HeightM = Slave.Height },
    };
    if (calibrated)
    {
      foreach (var (id, east, north) in Landmarks)
      {
        var (pan, tilt) = SlaveReading(east, north);
        var (mlat, mlon) = Wgs84(east, north);
        response.Observations.Add(new GeoObservation
        {
          LandmarkId = id, LandmarkName = id, Lat = mlat, Lon = mlon, Pan = (float)pan, Tilt = (float)tilt,
        });
      }
    }
    return CameraSpatialModel.Build(response);
  }

  private static double Dot((double, double, double) a, (double, double, double) b) =>
    a.Item1 * b.Item1 + a.Item2 * b.Item2 + a.Item3 * b.Item3;

  private static (double, double, double) Cross((double, double, double) a, (double, double, double) b) =>
    (a.Item2 * b.Item3 - a.Item3 * b.Item2, a.Item3 * b.Item1 - a.Item1 * b.Item3, a.Item1 * b.Item2 - a.Item2 * b.Item1);
}

public class GeoAimingTests
{
  private static GeoAimPair Pair() => new(TwoCameraWorld.MasterModel(), TwoCameraWorld.SlaveModel());

  [Theory]
  [InlineData(0, 0)]
  [InlineData(-8, 4)]
  [InlineData(8, 16)]
  [InlineData(-5, 20)]
  [InlineData(3, -2)]
  public void SendsTheSlaveWhereTheMastersSpotIsOnTheGround(double east, double north)
  {
    var (x, y) = TwoCameraWorld.InMaster(east, north);

    var projection = GeoAiming.Project(Pair(), x, y);

    Assert.NotNull(projection);
    var (pan, tilt) = TwoCameraWorld.SlaveReading(east, north);
    // 1e-4 generic is 0.018° of pan: the chain adds nothing a person could see.
    Assert.Equal(pan, projection.Aim.Pan, 4);
    Assert.Equal(tilt, projection.Aim.Tilt, 4);
    var (lat, lon) = TwoCameraWorld.Wgs84(east, north);
    Assert.Equal(lat, projection.Lat, 7);
    Assert.Equal(lon, projection.Lon, 7);
    Assert.False(projection.Aim.Extrapolated);
  }

  [Fact]
  public void RefusesASpotAboveTheMastersHorizon()
  {
    // The master looks 18° down with a vertical half-angle of about 21.5°, so the top tenth of its
    // picture is sky.
    Assert.Null(GeoAiming.Project(Pair(), 0.5, 0.02));
  }

  [Fact]
  public void NamesWhatIsMissing()
  {
    var master = TwoCameraWorld.MasterModel();
    var slave = TwoCameraWorld.SlaveModel();

    Assert.Null(GeoAiming.Unusable("master", master, slave));
    Assert.Contains("reference placement", GeoAiming.Unusable("master", master, null));
    Assert.Contains("Master 'master'", GeoAiming.Unusable("master", null, slave));
    Assert.Contains("shares no coordinates", GeoAiming.Unusable(
      "master",
      TwoCameraWorld.MasterModel(TwoCameraWorld.Map("office", real: false)),
      TwoCameraWorld.SlaveModel(TwoCameraWorld.Map("yard", real: false))));
    Assert.Contains("Master 'master'", GeoAiming.Unusable("master", TwoCameraWorld.MasterModel(marked: false), slave));
    // The slave's own fit says what it lacks, in the words the calibration wizard uses.
    Assert.Contains("landmarks", GeoAiming.Unusable("master", master, TwoCameraWorld.SlaveModel(calibrated: false)));
  }

  [Fact]
  public void CarriesTheMasterFloorToTheSlave()
  {
    var floor = new Space { Id = "deck", Name = "deck", Kind = SpaceKind.Plan, RealCoordinates = true, ElevationM = 0 };
    var pair = new GeoAimPair(TwoCameraWorld.MasterModel(floor), TwoCameraWorld.SlaveModel(floor));

    Assert.Equal(0, pair.Master.GroundFit!.FloorElevationM);
    var (x, y) = TwoCameraWorld.InMaster(2, 8);
    Assert.NotNull(GeoAiming.Project(pair, x, y));
  }
}
