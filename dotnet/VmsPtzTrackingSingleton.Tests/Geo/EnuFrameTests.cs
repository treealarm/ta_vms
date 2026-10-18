using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton.Tests.Geo;

public class EnuFrameTests
{
  private static readonly EnuFrame Frame = new(41.3111, 69.2797);

  [Fact]
  public void RoundTripsAPoint()
  {
    var (lat, lon) = Frame.FromEnu(Frame.ToEnu(41.3149, 69.2861));

    Assert.Equal(41.3149, lat, 9);
    Assert.Equal(69.2861, lon, 9);
  }

  [Fact]
  public void TheOriginIsZero()
  {
    var origin = Frame.ToEnu(Frame.OriginLat, Frame.OriginLon);

    Assert.Equal(0, origin.East, 9);
    Assert.Equal(0, origin.North, 9);
  }

  [Fact]
  public void MatchesTheGreatCircleDistanceAcrossAScene()
  {
    // 500 m out, a large scene: the flat approximation must agree with the sphere to a
    // few centimetres, or a camera's aim drifts with distance from the origin.
    var (lat, lon) = Frame.FromEnu(new Enu(300, 400));
    var flat = Math.Sqrt(300 * 300 + 400 * 400);

    Assert.InRange(Haversine(Frame.OriginLat, Frame.OriginLon, lat, lon) - flat, -0.05, 0.05);
  }

  private static double Haversine(double lat1, double lon1, double lat2, double lon2)
  {
    const double rad = Math.PI / 180;
    var dLat = (lat2 - lat1) * rad;
    var dLon = (lon2 - lon1) * rad;
    var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin(dLon / 2), 2);
    return 2 * EnuFrame.EarthRadiusM * Math.Asin(Math.Sqrt(a));
  }
}
