using Vms.Cfg;
using VmsMap.Clients;

namespace VmsMap.Tests;

// The catalog is the map's whole view of a camera, so a flag the map will act on has to survive
// this hop: VmsCfg keeps it, and everything downstream — the follower, the calibration refusal —
// reads it from CameraInfo alone.
public class CameraCatalogTests
{
  [Fact]
  public void TheGeoFromDeviceFlagReachesTheMap()
  {
    var info = VmsCfgCameraCatalog.ToInfo(new CameraSetup
    {
      CameraId = "cam1",
      Name = "Front door",
      ServerId = "srv1",
      GeoFromDevice = true,
    });

    Assert.True(info.GeoFromDevice);
    Assert.Equal("cam1", info.CameraId);
  }

  [Fact]
  public void ACameraWithoutTheFlagKeepsItsPlace()
  {
    var info = VmsCfgCameraCatalog.ToInfo(new CameraSetup { CameraId = "cam1", ServerId = "srv1" });

    Assert.False(info.GeoFromDevice);
  }
}
