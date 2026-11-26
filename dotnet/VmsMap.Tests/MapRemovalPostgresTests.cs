using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

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
  private const string CanvasName = "canvas";
  private const string OwnerName = "panel";
  private const string AdminRole = "admin";
  private const int LockWaitMs = 750;

  private sealed class TestContext : ServerCallContext
  {
    protected override string MethodCore => "test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "peer";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => [];
    protected override CancellationToken CancellationTokenCore => CancellationToken.None;
    protected override Metadata ResponseTrailersCore => [];
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
      throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception ex)
    {
      return ex;
    }
  }

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

  [Fact]
  public async Task ACopyCreatedWhileItsOwnerIsRemovedDoesNotOutliveIt()
  {
    await using var first = await MapPostgres.OpenAsync(fresh: true);
    if (first is null)
      return;

    var canvas = new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(Guid.NewGuid()), name = CanvasName };
    var owner = new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(Guid.NewGuid()), name = OwnerName };
    await new MapDb(first.Db).UpdateHierarchyAsync([canvas, owner]);
    var ownerGuid = Guid.Parse(owner.id!);

    await using var second = await MapPostgres.OpenAsync();

    // The two real operations, on two replicas of VmsMap: a copy of the owner is asked for while
    // the owner itself is being deleted.
    var errors = await Task.WhenAll(
      Capture(async () =>
      {
        await MapPostgres.Grpc(second!.Db).CreateReplica(
          new MapProto.CreateReplicaRequest
          {
            OwnerId = owner.id!,
            ParentId = canvas.id!,
            Caller = new MapProto.Caller { Roles = { AdminRole } },
          },
          new TestContext());
      }),
      Capture(async () =>
      {
        await MapPostgres.Spatial(first.Db).RemoveSubtreeAsync([owner.id!], CancellationToken.None);
      }));

    // Whichever went first, the answer is the same: a copy made before the removal is deleted with
    // its owner, and one asked for after it finds no owner to copy.
    Assert.Null(errors[1]);
    Assert.True(
      errors[0] is null or RpcException { StatusCode: StatusCode.InvalidArgument },
      errors[0]?.ToString());
    Assert.False(
      await first.Db.Markers.AsNoTracking().AnyAsync(m => m.id == ownerGuid || m.owner_id == ownerGuid));
  }

  [Fact]
  public async Task RemovingACopyLeavesTheDiagramAndTheOtherCopies()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;

    var db = connection.Db;
    var canvas = new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(Guid.NewGuid()), name = CanvasName };
    var diagram = new BaseMarkerDTO { id = Utils.ConvertGuidToObjectId(Guid.NewGuid()), name = OwnerName };
    var left = new BaseMarkerDTO
    {
      id = Utils.ConvertGuidToObjectId(Guid.NewGuid()),
      name = "left copy",
      parent_id = canvas.id,
      owner_id = diagram.id,
    };
    var right = new BaseMarkerDTO
    {
      id = Utils.ConvertGuidToObjectId(Guid.NewGuid()),
      name = "right copy",
      parent_id = canvas.id,
      owner_id = diagram.id,
    };
    await new MapDb(db).UpdateHierarchyAsync([canvas, diagram, left, right]);
    var diagramId = Guid.Parse(diagram.id!);
    var leftId = Guid.Parse(left.id!);
    db.Diagrams.AddRange(
      new DBDiagram { id = diagramId },
      new DBDiagram { id = leftId },
      new DBDiagram { id = Guid.Parse(right.id!) });
    await db.SaveChangesAsync();

    // A copy is deleted from wherever it stands: it is nobody's owner and nothing stands under it,
    // so the diagram it shows and the other copies of it are not part of what goes.
    await MapPostgres.Spatial(db).RemoveSubtreeAsync([left.id!], CancellationToken.None);

    Assert.False(await db.Markers.AnyAsync(m => m.id == leftId));
    Assert.False(await db.Diagrams.AnyAsync(d => d.id == leftId));
    Assert.True(await db.Markers.AnyAsync(m => m.id == diagramId));
    Assert.True(await db.Diagrams.AnyAsync(d => d.id == diagramId));
    Assert.True(await db.Markers.AnyAsync(m => m.id == Guid.Parse(right.id!)));
  }
}
