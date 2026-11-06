namespace VmsSingleton.Tests;

using VmsSingleton.EventRouting;

public class SubscriptionMatcherTests
{
  private static EventSelector Selector(
    string[]? objectIds = null,
    string[]? cameraIds = null,
    string[]? kinds = null,
    string[]? tokens = null,
    Dictionary<string, string>? props = null,
    double? minConfidence = null) =>
    new(objectIds ?? [], cameraIds ?? [], kinds ?? [], tokens ?? [],
      props ?? new Dictionary<string, string>(), minConfidence);

  [Fact]
  public void EveryFieldIsAndedWithTheOthers()
  {
    var subscription = RoutingTestData.Subscription(selector: Selector(
      objectIds: ["00000000-0000-0000-0000-0000000000e5"],
      cameraIds: [RoutingTestData.CameraA],
      kinds: ["digital_input"],
      tokens: ["input1"],
      props: new Dictionary<string, string> { ["contact"] = "closed" }));

    Assert.True(SubscriptionMatcher.Selects(subscription, RoutingTestData.Event()));

    // Each field alone can refuse the same event.
    Assert.False(SubscriptionMatcher.Selects(
      RoutingTestData.Subscription(selector: Selector(objectIds: ["other"])),
      RoutingTestData.Event()));
    Assert.False(SubscriptionMatcher.Selects(
      RoutingTestData.Subscription(eventName: "relay", selector: Selector()),
      RoutingTestData.Event()));
  }

  [Fact]
  public void AnEmptyListIsAny()
  {
    var subscription = RoutingTestData.Subscription(selector: Selector());

    Assert.True(SubscriptionMatcher.Selects(subscription, RoutingTestData.Event()));
  }

  [Fact]
  public void PropsAreStringEquality()
  {
    var closed = RoutingTestData.Subscription(selector: Selector(
      props: new Dictionary<string, string> { ["contact"] = "closed" }));
    var open = RoutingTestData.Subscription(selector: Selector(
      props: new Dictionary<string, string> { ["contact"] = "open" }));

    Assert.True(SubscriptionMatcher.Selects(closed, RoutingTestData.Event()));
    Assert.False(SubscriptionMatcher.Selects(open, RoutingTestData.Event()));
  }

  [Fact]
  public void MinConfidenceIsAFloorNotEquality()
  {
    var eventWith = RoutingTestData.Event(
      eventName: "analytics",
      props: new Dictionary<string, string> { ["confidence"] = "0.82" });

    Assert.True(SubscriptionMatcher.Selects(
      RoutingTestData.Subscription(eventName: "analytics", selector: Selector(minConfidence: 0.6)),
      eventWith));
    Assert.False(SubscriptionMatcher.Selects(
      RoutingTestData.Subscription(eventName: "analytics", selector: Selector(minConfidence: 0.9)),
      eventWith));
  }

  [Fact]
  public void AFieldTheEventDoesNotCarryNeverMatchesAConfiguredList()
  {
    // The event is a sensor's, so it has no `active`; "no value" is not "any value".
    var subscription = RoutingTestData.Subscription(selector: Selector(
      props: new Dictionary<string, string> { ["active"] = "true" }));

    Assert.False(SubscriptionMatcher.Selects(subscription, RoutingTestData.Event()));
  }

  [Fact]
  public void CameraIdsAreMatchedThroughTheCameraIdProp()
  {
    var other = RoutingTestData.Subscription(selector: Selector(cameraIds: [RoutingTestData.CameraB]));

    Assert.False(SubscriptionMatcher.Selects(other, RoutingTestData.Event()));
  }

  [Theory]
  [InlineData("any", true, true)]
  [InlineData("any", false, true)]
  [InlineData("any", null, true)]
  [InlineData("armed", true, true)]
  [InlineData("armed", false, false)]
  [InlineData("disarmed", false, true)]
  [InlineData("disarmed", true, false)]
  [InlineData("armed", null, false)]
  [InlineData("disarmed", null, false)]
  public void ArmedConditionNeedsAKnownAnswer(string condition, bool? armed, bool expected)
  {
    var subscription = RoutingTestData.Subscription(armedCondition: condition);

    Assert.Equal(expected, SubscriptionMatcher.ArmedConditionMatches(subscription, armed));
  }
}
