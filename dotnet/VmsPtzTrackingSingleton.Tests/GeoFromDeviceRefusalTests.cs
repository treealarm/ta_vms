using VmsPtzTrackingSingleton;

namespace VmsPtzTrackingSingleton.Tests;

// A fit says where a camera stood when its landmarks were marked. A camera following its device
// moves away from that point, so both kinds of calibrated aim — the slave pair and the map point —
// have to refuse rather than send the motor somewhere plausible and wrong.
public class GeoFromDeviceRefusalTests
{
  private static CameraPtzRouter.Route Route(bool geoFromDevice) =>
    new("vmsonvif", "profile1", HasTelemetry: true, GeoFromDevice: geoFromDevice);

  [Fact]
  public void ACameraFollowingItsDeviceCannotBeAimedByACalibration()
  {
    Assert.NotNull(TrackingSessionManager.DevicePositionRefusal(Route(geoFromDevice: true)));
  }

  [Fact]
  public void AHandPlacedCameraIsUnaffected()
  {
    Assert.Null(TrackingSessionManager.DevicePositionRefusal(Route(geoFromDevice: false)));
    // No route at all says nothing about the flag, and must not be read as one.
    Assert.Null(TrackingSessionManager.DevicePositionRefusal((CameraPtzRouter.Route?)null));
  }

  [Fact]
  public void EitherSideOfAPairRefusesTheAim()
  {
    Assert.NotNull(TrackingSessionManager.DevicePositionRefusal(Route(false), Route(true)));
    Assert.NotNull(TrackingSessionManager.DevicePositionRefusal(Route(true), Route(false)));
    Assert.Null(TrackingSessionManager.DevicePositionRefusal(Route(false), Route(false)));
  }
}
