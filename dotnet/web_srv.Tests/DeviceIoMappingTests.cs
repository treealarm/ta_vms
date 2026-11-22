using web_srv;

namespace web_srv.Tests;

// A part's identity and its behaviour are two writes: the name is VmsCfg's row, the arming and
// alarm rule are the map's. These tests lock the split, so a rename cannot carry a stale rule.
public class DeviceIoMappingTests
{
  private const string CameraId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

  private static DeviceSettingsBody Body(
    string kind = DeviceIoRequests.DigitalInputKind,
    string token = "input1",
    string? name = "sensor1",
    string? armMode = "armed",
    string? triggerOn = "closed",
    bool alarmEnabled = true) =>
    new(kind, token, name, armMode!, triggerOn, alarmEnabled);

  [Fact]
  public void ARequestWithoutANameDoesNotWriteVmsCfg()
  {
    // The behaviour card names the part elsewhere; a null name must not blank the saved one.
    Assert.Null(DeviceIoRequests.NameRequest(CameraId, Body(name: null)));
  }

  [Fact]
  public void ANameRequestCarriesOnlyTheIdentity()
  {
    var request = DeviceIoRequests.NameRequest(CameraId, Body());

    Assert.NotNull(request);
    Assert.Equal(CameraId, request!.Settings.CameraId);
    Assert.Equal("input1", request.Settings.Token);
    Assert.Equal("sensor1", request.Settings.Name);
  }

  [Fact]
  public void ABehaviourRequestCarriesTheRuleAndToleratesNulls()
  {
    var request = DeviceIoRequests.BehaviourRequest(
      CameraId, Body(armMode: null, triggerOn: null));

    Assert.NotNull(request);
    Assert.Equal(string.Empty, request!.ArmMode);
    Assert.Equal(string.Empty, request.TriggerOn);
    Assert.True(request.AlarmEnabled);
    Assert.Equal(CameraId, request.Key.SourceId);
    Assert.Equal("input1", request.Key.PartToken);
  }

  [Fact]
  public void ARelayCarriesNoArmingOrAlarmRule()
  {
    Assert.Null(DeviceIoRequests.BehaviourRequest(
      CameraId, Body(kind: "relay", token: "lamp")));
  }
}
