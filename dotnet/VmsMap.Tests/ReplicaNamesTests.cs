using VmsMap.Domain;
using Xunit;

namespace VmsMap.Tests;

// A replica nobody named is named by the server: its owner and its own number. The number is the
// one after the highest in use, so a removal in the middle cannot make two pictures of one node
// carry the same name.
public class ReplicaNamesTests
{
  [Fact]
  public void TheFirstReplicaOfAnOwnerIsTheFirst()
  {
    Assert.Equal("cam1 rep1", ReplicaNames.Next("cam1", []));
  }

  [Fact]
  public void TheNextReplicaFollowsTheHighestNumberInUse()
  {
    Assert.Equal("relay4 rep2", ReplicaNames.Next("relay4", ["relay4 rep1"]));
    Assert.Equal("relay4 rep4", ReplicaNames.Next("relay4", ["relay4 rep1", "relay4 rep3"]));
  }

  [Fact]
  public void OnlyNamesOfThatOwnerCount()
  {
    Assert.Equal("cam1 rep1",
      ReplicaNames.Next("cam1", ["cam1", "cam12 rep7", "cam1 rack", "other rep9", null]));
  }
}
