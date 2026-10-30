using System;
using System.Collections.Generic;
using System.Linq;
using VmsMap.Db;

namespace VmsMap.Tests;

public class AlarmAggregationTests
{
  private static readonly Guid Root = Guid.Parse("00000000-0000-0000-0000-000000000001");
  private static readonly Guid Floor = Guid.Parse("00000000-0000-0000-0000-000000000002");
  private static readonly Guid Leaf = Guid.Parse("00000000-0000-0000-0000-000000000003");
  private static readonly Guid Sibling = Guid.Parse("00000000-0000-0000-0000-000000000004");

  private static AlarmNode Node(
    Guid id,
    Guid? parent,
    bool ownAlarm = false,
    bool storedAlarm = false,
    int storedChildren = 0) =>
    new(id, parent, ownAlarm, storedAlarm, storedChildren);

  [Fact]
  public void AlarmOnALeafRaisesEveryAncestor()
  {
    var nodes = new[]
    {
      Node(Root, null),
      Node(Floor, Root),
      Node(Leaf, Floor, ownAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Leaf, Floor, Root });

    Assert.Equal((true, 0), updates[Leaf]);
    Assert.Equal((false, 1), updates[Floor]);
    Assert.Equal((false, 1), updates[Root]);
  }

  [Fact]
  public void TwoAlarmedChildrenCountAsTwo()
  {
    var nodes = new[]
    {
      Node(Floor, null),
      Node(Leaf, Floor, ownAlarm: true),
      Node(Sibling, Floor, ownAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Leaf, Sibling, Floor });

    Assert.Equal((false, 2), updates[Floor]);
  }

  [Fact]
  public void ClearingTheLastAlarmLowersTheAncestors()
  {
    var nodes = new[]
    {
      Node(Root, null, storedChildren: 1),
      Node(Floor, Root, storedChildren: 1),
      Node(Leaf, Floor, ownAlarm: false, storedAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Leaf, Floor, Root });

    Assert.Equal((false, 0), updates[Leaf]);
    Assert.Equal((false, 0), updates[Floor]);
    Assert.Equal((false, 0), updates[Root]);
  }

  [Fact]
  public void RepeatingTheSameStateChangesNothing()
  {
    var nodes = new[]
    {
      Node(Root, null, storedAlarm: false, storedChildren: 1),
      Node(Leaf, Root, ownAlarm: true, storedAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Leaf, Root });

    Assert.Empty(updates);
  }

  [Fact]
  public void ADeletedAlarmedChildLowersItsParent()
  {
    // The child row is gone from `objects`, so only the parent and its remaining children are
    // in the set; the parent's stored counter still counts the deleted child.
    var nodes = new[]
    {
      Node(Floor, null, storedChildren: 1)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Floor });

    Assert.Equal((false, 0), updates[Floor]);
  }

  [Fact]
  public void AMoveRecomputesBothParents()
  {
    // Before the move: Floor counted Leaf (alarmed), Root counted Floor.
    // After it: Leaf is a child of Root, and Floor has no alarmed children left.
    var nodes = new[]
    {
      Node(Root, null, storedChildren: 1),
      Node(Floor, Root, storedChildren: 1),
      Node(Leaf, Root, ownAlarm: true, storedAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Root, Floor });

    Assert.Equal((false, 0), updates[Floor]);
    // Root still counts one alarmed child: Leaf replaced Floor, which is no longer alarmed.
    Assert.False(updates.ContainsKey(Root));
  }

  [Fact]
  public void AnAlarmedChildDeepBelowIsCountedThroughItsParent()
  {
    var nodes = new[]
    {
      Node(Root, null, storedChildren: 0),
      Node(Floor, Root, storedAlarm: false, storedChildren: 1),
      Node(Leaf, Floor, ownAlarm: true)
    };

    var updates = AlarmAggregation.Compute(nodes, new HashSet<Guid> { Root });

    Assert.Equal((false, 1), updates[Root]);
  }

  [Fact]
  public void ACycleInParentIdsIsRejected()
  {
    var a = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    var b = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    var nodes = new[]
    {
      Node(a, b),
      Node(b, a)
    };

    Assert.Throws<InvalidOperationException>(() =>
      AlarmAggregation.Compute(nodes, new HashSet<Guid> { a, b }));
  }
}
