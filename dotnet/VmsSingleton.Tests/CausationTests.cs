namespace VmsSingleton.Tests;

using VmsSingleton.EventRouting;

public class CausationTests
{
  [Fact]
  public void ASubscriptionMayNotFireTwiceInOneChain()
  {
    // The chain so far is A → B; A is refused, B is not.
    var evt = RoutingTestData.Event(props: new Dictionary<string, string>
    {
      ["depth"] = "2",
      ["causation_chain"] = "A,B",
    });

    var chain = CausationChain.FromEvent(evt);

    Assert.Equal(2, chain.Depth);
    Assert.False(chain.CanFire("A"));
    Assert.True(chain.CanFire("C"));
  }

  [Fact]
  public void AChainDeeperThanTheLimitStops()
  {
    var atLimit = CausationChain.FromEvent(RoutingTestData.Event(
      props: new Dictionary<string, string> { ["depth"] = Routing.MaxDepth.ToString() }));
    var overLimit = CausationChain.FromEvent(RoutingTestData.Event(
      props: new Dictionary<string, string> { ["depth"] = (Routing.MaxDepth + 1).ToString() }));

    Assert.True(atLimit.CanFire("A"));
    Assert.False(overLimit.CanFire("A"));
  }

  [Fact]
  public void AfterExtendsTheChainAndTheDepth()
  {
    var chain = CausationChain.FromEvent(RoutingTestData.Event(
      props: new Dictionary<string, string> { ["depth"] = "1", ["causation_chain"] = "A" }));

    var after = chain.After("B");

    Assert.Equal(2, after.Depth);
    Assert.Equal("A,B", after.Serialize());
    Assert.False(after.CanFire("A"));
    Assert.False(after.CanFire("B"));
  }

  [Fact]
  public void AnEventOutsideASubscriptionStartsAtDepthZero()
  {
    var chain = CausationChain.FromEvent(RoutingTestData.Event());

    Assert.Equal(0, chain.Depth);
    Assert.Empty(chain.SubscriptionIds);
    Assert.True(chain.CanFire("anything"));
  }
}
