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
    Assert.Contains("\"contact\":\"open\"", subscription.SourceFilter);
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
    Assert.Equal(DeviceSettingsRules.TriggerOpen, settings.TriggerOn);
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
}
