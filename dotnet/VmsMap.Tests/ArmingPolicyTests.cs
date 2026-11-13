using System;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

// The policy on its own: which node kinds take part in arming, and with which modes. The
// distinction it keeps is between "this kind can be armed" and "this one is configured".
public class ArmingPolicyTests
{
  [Fact]
  public void ACameraAndAnInputTakeOperatorModesOnly()
  {
    foreach (var part in new[] { DeviceObjectId.DevicePart, DeviceObjectId.DigitalInputPart })
    {
      var armability = ArmingPolicy.ForPart(part, configured: true)!;

      Assert.True(armability.Armable);
      Assert.Equal([ArmMode.Armed, ArmMode.Disarmed], armability.Modes);
      // Inherit is the administrator's setting, not a command an operator sends.
      Assert.False(armability.Accepts(ArmMode.Inherit));
    }
  }

  [Fact]
  public void AnUnconfiguredInputIsArmableInKindButNotConfigured()
  {
    var armability = ArmingPolicy.ForPart(DeviceObjectId.DigitalInputPart, configured: false)!;

    Assert.True(armability.Armable);
    Assert.False(armability.Configured);
  }

  [Fact]
  public void ARelayHasNoArming()
  {
    var armability = ArmingPolicy.ForPart(DeviceObjectId.RelayPart, configured: true)!;

    Assert.False(armability.Armable);
    Assert.Empty(armability.Modes);
  }

  [Fact]
  public void ASpaceIsAContainerWhateverStandsInIt()
  {
    var armability = ArmingPolicy.ForContainer(NodeKind.Space, hasDevicesInSubtree: false);

    Assert.True(armability.Armable);
    Assert.Equal([ArmMode.Inherit, ArmMode.Armed, ArmMode.Disarmed], armability.Modes);
  }

  [Fact]
  public void AnotherContainerIsAZoneOnlyWhenDevicesStandThroughIt()
  {
    foreach (var kind in new[] { NodeKind.Figure, NodeKind.Object, NodeKind.Diagram })
    {
      Assert.True(ArmingPolicy.ForContainer(kind, hasDevicesInSubtree: true).Armable);
      Assert.False(ArmingPolicy.ForContainer(kind, hasDevicesInSubtree: false).Armable);
    }
  }

  [Fact]
  public void ALandmarkIsNeverAContainer()
  {
    Assert.False(ArmingPolicy.ForContainer(NodeKind.Landmark, hasDevicesInSubtree: true).Armable);
  }

  [Theory]
  [InlineData(ArmMode.Armed, true)]
  [InlineData(ArmMode.Disarmed, false)]
  public void ModesAreStoredAsTheBooleanTheResolverReads(string mode, bool stored)
  {
    Assert.Equal(stored, ArmMode.Value(mode));
  }

  [Fact]
  public void InheritIsStoredAsNoRowAtAll()
  {
    Assert.Null(ArmMode.Value(ArmMode.Inherit));
    Assert.False(ArmMode.IsKnown("always"));
  }
}
