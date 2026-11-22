using System.Text.Json;
using Vms.Onvif;
using web_srv;

namespace web_srv.Tests;

// The device I/O rows are what the monitor tile and the device panel read. The lists moved behind
// VmsOnvif's presence cache; the JSON did not change, and these tests are what keeps it that way —
// a field silently renamed or dropped reads as an empty panel, not as a broken build.
public class DeviceIoJsonContractTests
{
  private static JsonElement Json(object row) =>
    JsonSerializer.SerializeToElement(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));

  private static string[] Keys(JsonElement row) =>
    row.EnumerateObject().Select(p => p.Name).ToArray();

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void ARelayRowKeepsTheFieldsTheTileReads(bool liveActive)
  {
    var row = Json(DeviceIoRows.Relay(
      new RelayOutput
      {
        Token = "relay1",
        Mode = RelayMode.Bistable,
        IdleState = IdleState.Open,
        DelayMs = 1500,
        LiveActive = liveActive,
        Pending = true,
      },
      canControl: true));

    Assert.Equal(
      ["token", "mode", "idleState", "delayMs", "liveActive", "livePending", "canControl"],
      Keys(row));
    Assert.Equal("relay1", row.GetProperty("token").GetString());
    Assert.Equal("bistable", row.GetProperty("mode").GetString());
    Assert.Equal("open", row.GetProperty("idleState").GetString());
    Assert.Equal(1500, row.GetProperty("delayMs").GetInt32());
    Assert.True(row.GetProperty("livePending").GetBoolean());
    Assert.True(row.GetProperty("canControl").GetBoolean());
  }

  [Fact]
  public void ARelayTheCameraHasNotReportedKeepsItsLiveStateUnknown()
  {
    var row = Json(DeviceIoRows.Relay(new RelayOutput { Token = "relay1" }, canControl: false));

    // "No event yet" is not "inactive": the field is present and null, which is what the tile
    // draws as unknown.
    Assert.Equal(JsonValueKind.Null, row.GetProperty("liveActive").ValueKind);
  }

  [Fact]
  public void AnInputRowKeepsTheFieldsThePanelReads()
  {
    var row = Json(DeviceIoRows.Input(
      new DigitalInput
      {
        Token = "input1",
        IdleState = IdleState.Closed,
        HasIdleState = true,
        LiveActive = false,
      },
      new DeviceIoRows.InputSettings(
        ArmMode: "armed",
        AlarmEnabled: true,
        Armed: true,
        ArmingSource: "zone",
        Bypassed: true,
        BypassUntil: DateTimeOffset.UnixEpoch,
        CanControl: true)));

    Assert.Equal(
      ["token", "idleState", "liveActive", "armMode", "alarmEnabled", "armed",
       "armingSource", "bypassed", "bypassUntil", "canControl"],
      Keys(row));
    Assert.Equal("input1", row.GetProperty("token").GetString());
    Assert.Equal("closed", row.GetProperty("idleState").GetString());
    Assert.False(row.GetProperty("liveActive").GetBoolean());
    Assert.Equal("armed", row.GetProperty("armMode").GetString());
    Assert.True(row.GetProperty("alarmEnabled").GetBoolean());
    Assert.True(row.GetProperty("armed").GetBoolean());
    Assert.Equal("zone", row.GetProperty("armingSource").GetString());
    Assert.True(row.GetProperty("bypassed").GetBoolean());
    Assert.True(row.GetProperty("canControl").GetBoolean());
  }

  [Fact]
  public void AnInputWithoutAReportedIdleStateKeepsItNull()
  {
    var row = Json(DeviceIoRows.Input(new DigitalInput { Token = "input1" }, default));

    // has_idle_state=false means the camera did not say, not that the contact rests closed.
    Assert.Equal(JsonValueKind.Null, row.GetProperty("idleState").ValueKind);
    Assert.Equal(JsonValueKind.Null, row.GetProperty("liveActive").ValueKind);
    // The part has no map settings yet either: unknown, never "off guard".
    Assert.Equal(JsonValueKind.Null, row.GetProperty("armed").ValueKind);
    Assert.Equal(JsonValueKind.Null, row.GetProperty("bypassUntil").ValueKind);
  }
}
