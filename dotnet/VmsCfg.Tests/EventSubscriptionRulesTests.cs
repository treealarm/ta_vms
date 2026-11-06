using Vms.Cfg;

namespace VmsCfg.Tests;

// The selector and the action parameters are JSON, so these are the checks that turn an unknown
// key into a message instead of a rule that never fires. No database is involved: the rules are
// the same whether a row arrives through the API or an import.
public class EventSubscriptionRulesTests
{
  private const string Camera = "11111111-1111-1111-1111-111111111111";
  private const string OtherCamera = "22222222-2222-2222-2222-222222222222";

  private static EventSubscription Valid() => new()
  {
    Name = "demo-sensor-recording",
    Enabled = true,
    EventName = "sensor",
    SourceFilter = Selector(Camera),
    ArmedCondition = "any",
    CooldownMs = 0,
    Actions =
    {
      new SubscriptionAction
      {
        Position = 0,
        Kind = "retain_recording",
        Params = Retain(Camera),
      },
    },
  };

  private static string Selector(string cameraId) =>
    "{\"camera_ids\":[\"" + cameraId + "\"],\"kinds\":[\"digital_input\"],"
    + "\"props\":{\"contact\":\"closed\"}}";

  private static string Retain(string cameraId) =>
    "{\"cameras\":[\"" + cameraId + "\",\"source_camera\"],\"timeout_seconds\":30}";

  [Fact]
  public void AValidSubscriptionPassesAndNamesItsCameras()
  {
    var check = EventSubscriptionRules.Check(Valid());

    Assert.Null(check.Error);
    Assert.Equal([Camera], check.CameraIds);
  }

  [Theory]
  [InlineData("""{"nonsense":[]}""")]
  [InlineData("""{"object_ids":"not-an-array"}""")]
  [InlineData("""{"object_ids":["not-a-uuid"]}""")]
  [InlineData("""{"camera_ids":["not-a-uuid"]}""")]
  [InlineData("""{"kinds":["motion"]}""")]
  [InlineData("""{"props":{"contact":null}}""")]
  [InlineData("""{"props":{"contact":["closed"]}}""")]
  [InlineData("""{"props":{"contact":{"value":"closed"}}}""")]
  [InlineData("""{"props":{"min_confidence":"high"}}""")]
  [InlineData("""{"props":{"min_confidence":2}}""")]
  [InlineData("not json")]
  public void ABadSelectorIsRefused(string selector)
  {
    var subscription = Valid();
    subscription.SourceFilter = selector;

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Theory]
  [InlineData("onvif-event")]
  [InlineData("")]
  public void AnUnknownEventNameIsRefused(string eventName)
  {
    var subscription = Valid();
    subscription.EventName = eventName;

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Fact]
  public void AnUnknownArmedConditionIsRefused()
  {
    var subscription = Valid();
    subscription.ArmedCondition = "sometimes";

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Theory]
  [InlineData("""{"kind":"send_sms","params":{}}""")]
  [InlineData("""{"kind":"set_relay","params":{}}""")]
  [InlineData("""{"kind":"set_relay","params":{"camera_id":"not-a-uuid","token":"lamp","active":true}}""")]
  [InlineData("""{"kind":"set_relay","params":{"camera_id":"11111111-1111-1111-1111-111111111111","token":"lamp","active":"yes"}}""")]
  [InlineData("""{"kind":"retain_recording","params":{"cameras":[],"timeout_seconds":30}}""")]
  [InlineData("""{"kind":"retain_recording","params":{"cameras":["11111111-1111-1111-1111-111111111111"],"timeout_seconds":0}}""")]
  [InlineData("""{"kind":"retain_recording","params":{"cameras":["11111111-1111-1111-1111-111111111111"],"timeout_seconds":30,"event_id":"{unknown}"}}""")]
  [InlineData("""{"kind":"retain_recording","params":{"cameras":["11111111-1111-1111-1111-111111111111"],"timeout_seconds":30,"extra":1}}""")]
  [InlineData("""{"kind":"emit_event","params":{"object_id":"elsewhere","event_name":"relay"}}""")]
  [InlineData("""{"kind":"emit_event","params":{"object_id":"source","event_name":"nonsense"}}""")]
  [InlineData("""{"kind":"raise_alarm","params":{"object_id":"source","definition":"fire"}}""")]
  public void ABadActionIsRefused(string actionJson)
  {
    var action = ActionFromJson(actionJson);
    var subscription = Valid();
    subscription.Actions.Clear();
    subscription.Actions.Add(action);

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Fact]
  public void ActionsMustBePositionedInOrder()
  {
    var subscription = Valid();
    subscription.Actions.Add(new SubscriptionAction
    {
      Position = 5,
      Kind = "emit_event",
      Params = """{"object_id":"source","event_name":"relay"}""",
    });

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Fact]
  public void ASubscriptionWithNoActionsIsRefused()
  {
    var subscription = Valid();
    subscription.Actions.Clear();

    Assert.NotNull(EventSubscriptionRules.Check(subscription).Error);
  }

  [Fact]
  public void SetRelayNamesAnotherCameraToLookUp()
  {
    var subscription = Valid();
    subscription.Actions.Add(new SubscriptionAction
    {
      Position = 1,
      Kind = "set_relay",
      Params = "{\"camera_id\":\"" + OtherCamera + "\",\"token\":\"lamp\",\"active\":true}",
    });

    var check = EventSubscriptionRules.Check(subscription);

    Assert.Null(check.Error);
    Assert.Equal([Camera, OtherCamera], check.CameraIds.OrderBy(x => x));
  }

  // Parses a one-action fixture: {"kind": "...", "params": {...}}.
  private static SubscriptionAction ActionFromJson(string json)
  {
    using var document = System.Text.Json.JsonDocument.Parse(json);
    var kind = document.RootElement.GetProperty("kind").GetString()!;

    if (kind == "send_sms")
    {
      // Not a known kind at all; the params are irrelevant.
      return new SubscriptionAction { Position = 0, Kind = kind, Params = "{}" };
    }

    return new SubscriptionAction
    {
      Position = 0,
      Kind = kind,
      Params = document.RootElement.GetProperty("params").GetRawText(),
    };
  }
}
