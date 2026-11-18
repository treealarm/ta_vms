using Vms.Cfg;
using VmsMap.Clients;

namespace VmsMap.Tests;

// The catalog is the map's whole view of a camera, so a flag the map will act on has to survive
// this hop: VmsCfg keeps it, and everything downstream — the follower, the calibration refusal —
// reads it from CameraInfo alone.
public class CameraCatalogTests
{
  // The fixtures these tests share.
  private const string CameraOneId = "cam1";
  private const string ServerOneId = "srv1";
  private const string FrontDoorName = "front door";

  [Fact]
  public void TheGeoFromDeviceFlagReachesTheMap()
  {
    var info = VmsCfgCameraCatalog.ToInfo(new CameraSetup
    {
      CameraId = CameraOneId,
      Name = FrontDoorName,
      ServerId = ServerOneId,
      GeoFromDevice = true,
    });

    Assert.True(info.GeoFromDevice);
    Assert.Equal(CameraOneId, info.CameraId);
  }

  [Fact]
  public void ACameraWithoutTheFlagKeepsItsPlace()
  {
    var info = VmsCfgCameraCatalog.ToInfo(
      new CameraSetup { CameraId = CameraOneId, ServerId = ServerOneId });

    Assert.False(info.GeoFromDevice);
  }
}
