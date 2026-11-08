using Vms.Cfg;
using VmsCfg.Demo;

namespace VmsCfg.Tests;

// The demo writes through the same validation an administrator's rule does. A demo that failed it
// would install a world whose sensor never reacts, and the acceptance script would report the
// feature broken rather than the rule.
public class DemoInstallerTests
{
  [Fact]
  public void TheDemoSubscriptionPassesTheRules()
  {
    var check = EventSubscriptionRules.Check(DemoRouting.Describe());

    Assert.Null(check.Error);
    // Both action cameras and the selector's camera are looked up before saving.
    Assert.Contains(DemoCameraIds.For("sim-master"), check.CameraIds);
    Assert.Contains(DemoCameraIds.For("sim-ptz"), check.CameraIds);
  }

  [Fact]
  public void TheDemoSubscriptionIsTheSensorContactOfThePtz()
  {
    var subscription = DemoRouting.Describe();

    Assert.Equal("sensor", subscription.EventName);
    Assert.Contains(DemoCameraIds.For("sim-ptz"), subscription.SourceFilter);
    // The contact the wire produces when the peer relay closes.
    Assert.Contains("\"contact\":\"closed\"", subscription.SourceFilter);
    Assert.Equal(2, subscription.Actions.Count);
    Assert.Equal("retain_recording", subscription.Actions[0].Kind);
    Assert.Equal("set_relay", subscription.Actions[1].Kind);
    // The contact raises its own alarm; the retention action must not double it.
    Assert.Contains("\"raise_alarm\":false", subscription.Actions[0].Params);
  }

  [Fact]
  public void TheSensorSettingsAreWhatThePolicyAllows()
  {
    var settings = DemoDevices.SensorSettings();

    Assert.Null(DeviceSettingsRules.Validate(settings));
    Assert.Equal(DeviceSettingsRules.Armed, settings.ArmMode);
    Assert.Equal(DemoDevices.SensorTrigger, settings.TriggerOn);
    Assert.True(settings.AlarmEnabled);
  }

  [Fact]
  public void TheDemoIdsAreTheOnesTheWorldAndOnvifUse()
  {
    // The contract between VmsMap's DemoIds, this service and the simulator; changing the namespace
    // or the key renames every camera of every installed world.
    Assert.Equal("94681efe-57da-5093-b420-1bed3b28dae8", DemoCameraIds.For("sim-master"));
    Assert.Equal("9f097856-169d-58d4-9d8a-db7405e20549", DemoCameraIds.For("sim-ptz"));
  }

  [Fact]
  public void TheDemoInstallsTheSimulatorsFiveCameras()
  {
    // The world's cameras in its own order: OnvifSim hands out its ONVIF ports from 8101 by that
    // index, so the port is the position in this list, not a value chosen here.
    var plan = DemoCameras.Plan();

    Assert.Equal(
      ["sim-master", "sim-ptz", "sim-master2", "sim-ptz2", "sim-vehicle"],
      plan.Select(camera => camera.Name));
    Assert.Equal([8101, 8102, 8103, 8104, 8105], plan.Select(camera => camera.OnvifPort));
    Assert.Equal([false, true, false, true, false], plan.Select(camera => camera.HasTelemetry));
    // The car's camera takes its place from the device; the rest are bolted where the world puts them.
    Assert.Equal([false, false, false, false, true], plan.Select(camera => camera.GeoFromDevice));
    Assert.Equal(DemoCameraIds.For("sim-master"), plan[0].CameraId);
  }

  [Fact]
  public void ADemoCameraIsWhatTheAdministratorsFormWouldWrite()
  {
    var camera = DemoCameras.Plan()[1].ToProto("host.docker.internal", "vms");

    Assert.Equal(DemoCameraIds.For("sim-ptz"), camera.CameraId);
    Assert.Equal("OnvifSim Main room slave (PTZ)", camera.Name);
    Assert.Equal("vms", camera.ServerId);
    Assert.Equal("host.docker.internal", camera.OnvifConn.Ip);
    Assert.Equal(8102, camera.OnvifConn.Port);
    Assert.Equal("http://host.docker.internal:8102/onvif/device_service", camera.OnvifConn.Xaddr);
    Assert.Equal("admin", camera.Cred.User);
    Assert.Equal("admin", camera.Cred.Pass);
    Assert.True(camera.HasTelemetry);
    Assert.False(camera.GeoFromDevice);

    var stream = Assert.Single(camera.Streams);
    Assert.Equal("main", stream.StreamName);
    Assert.Equal("Profile_2", stream.ProfileToken);
    Assert.True(stream.WebRtc);
  }
}
