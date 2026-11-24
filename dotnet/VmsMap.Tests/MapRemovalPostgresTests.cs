using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The general removal — a node, a canvas, a space, whatever the administrator deletes — reads the
// replicas it has to take with it before it writes anything. A writer that creates one (the sweep
// reconciling a diagram, a placement, a copy made by hand) takes the map's transitive locks before
// its own read, so the removal has to take them before its read too; otherwise a replica created in
// between outlives the owner it names. The locks only exist on a real provider, and the test is
// skipped without VMS_MAP_TEST_DSN, as the other Postgres tests are.
[Collection("postgis")]
public class MapRemovalPostgresTests
{
  private const string NodeName = "node";
  private const int LockWaitMs = 750;

  [Fact]
  public async Task TheRemovalWaitsForTheMapLocksBeforeItReads()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var node = new BaseMarkerDTO
    {
      id = Utils.ConvertGuidToObjectId(Guid.NewGuid()),
      name = NodeName,
    };
    await new MapDb(first.Db).UpdateHierarchyAsync([node]);

    await using var second = await MapPostgres.OpenAsync();
    var spatial = MapPostgres.Spatial(second!.Db);

    // Another writer holds the map's transitive locks, as every writer of the map's rows does
    // before it reads what it is about to change.
    await using var held = await first.Db.Database.BeginTransactionAsync();
    await MapLocks.AcquireCameraStateAsync(first.Db);

    var removal = spatial.RemoveSubtreeAsync([node.id!], CancellationToken.None);
    var raced = await Task.WhenAny(removal, Task.Delay(TimeSpan.FromMilliseconds(LockWaitMs)));

    // The removal is waiting on the lock rather than reading the tree without it.
    Assert.NotSame(removal, raced);

    await held.RollbackAsync();
    Assert.Equal(1, await removal);
    Assert.False(await first.Db.Markers.AnyAsync(m => m.id == Guid.Parse(node.id!)));
  }
}
