using VmsMap.Domain;
using Xunit;

namespace VmsMap.Tests;

// A replica nobody named is named by the server: its owner and its own number. The number is the
// one after the highest in use, so a removal in the middle cannot make two pictures of one node
// carry the same name.
public class ReplicaNamesTests
{
  // The fixtures these tests share.
  private const string CameraName = "cam1";
  private const string CameraFirstReplicaName = "cam1 rep1";
  private const string RelayName = "relay4";
  private const string RelayFirstReplicaName = "relay4 rep1";

  [Fact]
  public void TheFirstReplicaOfAnOwnerIsTheFirst()
  {
    Assert.Equal(CameraFirstReplicaName, ReplicaNames.Next(CameraName, []));
  }

  [Fact]
  public void TheNextReplicaFollowsTheHighestNumberInUse()
  {
    Assert.Equal("relay4 rep2", ReplicaNames.Next(RelayName, [RelayFirstReplicaName]));
    Assert.Equal("relay4 rep4",
      ReplicaNames.Next(RelayName, [RelayFirstReplicaName, "relay4 rep3"]));
  }

  [Fact]
  public void OnlyNamesOfThatOwnerCount()
  {
    Assert.Equal(CameraFirstReplicaName,
      ReplicaNames.Next(CameraName, [CameraName, "cam12 rep7", "cam1 rack", "other rep9", null]));
  }
}
