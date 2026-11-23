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

  [Fact]
  public void OnlyTheNonDisabledStreamsReachTheMap()
  {
    const string MainStream = "main";
    const string SubStream = "sub";

    var info = VmsCfgCameraCatalog.ToInfo(new CameraSetup
    {
      CameraId = CameraOneId,
      ServerId = ServerOneId,
      Streams =
      {
        new CameraStream { StreamName = MainStream },
        new CameraStream { StreamName = SubStream, Disabled = true },
      },
    });

    Assert.Equal([MainStream], info.VideoStreams);
    Assert.True(info.HasConfiguredStreams);
  }

  [Fact]
  public void ACameraWithoutStreamsHasNoConfiguredStreams()
  {
    var info = VmsCfgCameraCatalog.ToInfo(
      new CameraSetup { CameraId = CameraOneId, ServerId = ServerOneId });

    Assert.Empty(info.VideoStreams!);
    Assert.False(info.HasConfiguredStreams);
  }

  [Fact]
  public void TheOnvifConnectionReachesTheMap()
  {
    const string OnvifAddress = "10.0.0.5";
    const int OnvifPort = 80;

    var withOnvif = VmsCfgCameraCatalog.ToInfo(new CameraSetup
    {
      CameraId = CameraOneId,
      ServerId = ServerOneId,
      OnvifConn = new OnvifConnection { Ip = OnvifAddress, Port = OnvifPort },
    });
    var withoutOnvif = VmsCfgCameraCatalog.ToInfo(
      new CameraSetup { CameraId = CameraOneId, ServerId = ServerOneId });

    Assert.True(withOnvif.HasOnvif);
    Assert.False(withoutOnvif.HasOnvif);
  }

  [Fact]
  public void ADisabledCameraReachesTheMapAsDisabled()
  {
    var info = VmsCfgCameraCatalog.ToInfo(
      new CameraSetup { CameraId = CameraOneId, ServerId = ServerOneId, Disabled = true });

    Assert.True(info.Disabled);
  }
}
