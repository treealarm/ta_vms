using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// A write through a copy is two rows in one transaction: the diagram's type and picture, and the
// copy's own place. The transaction is what keeps a refused place from leaving the diagram changed;
// only a real provider has one, hence Postgres.
[Collection("postgis")]
public class MapDiagramWritePostgresTests
{
  private const string BoardName = "board";
  private const string PanelName = "panel";
  private const string CopyName = "panel rep1";
  private const string OfferedRegion = "left";
  private const string MissingRegion = "missing";

  [Fact]
  public async Task ARefusedPlacementLeavesTheDiagramsContentUnchanged()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;
    var db = connection.Db;

    // `fresh` empties the tree's tables; the demo's own key mapping hangs off no foreign key and
    // would otherwise point the next demo test at spaces this one removed.
    await db.Database.ExecuteSqlRawAsync("TRUNCATE demo_config CASCADE;");

    var type = new DBDiagramType { id = Guid.NewGuid(), name = BoardName };
    type.regions.Add(new DBDiagramTypeRegion
    {
      diagram_type_id = type.id,
      region_key = OfferedRegion,
      geometry = new DBDiagramCoord { width = 0.5, height = 0.5 },
    });
    db.DiagramTypes.Add(type);
    await db.SaveChangesAsync();

    var board = Guid.NewGuid();
    var panel = Guid.NewGuid();
    var copy = Guid.NewGuid();
    db.Markers.AddRange(
      new DBMarker { id = board, name = BoardName },
      new DBMarker { id = panel, name = PanelName },
      new DBMarker { id = copy, parent_id = board, owner_id = panel, name = CopyName });
    await db.SaveChangesAsync();

    db.Diagrams.AddRange(
      new DBDiagram { id = board, dgr_type = type.id },
      new DBDiagram { id = panel },
      new DBDiagram { id = copy, region_id = OfferedRegion });
    await db.SaveChangesAsync();

    // The copy is written as a copy: its type goes to the diagram, its place is its own — and the
    // place names a region the board does not offer, so the whole request is refused.
    var refused = await Assert.ThrowsAsync<RpcException>(() => MapPostgres.Grpc(db).UpdateDiagrams(
      new MapProto.DiagramList
      {
        Diagrams =
        {
          new MapProto.Diagram
          {
            Id = Utils.ConvertGuidToObjectId(copy),
            DgrType = Utils.ConvertGuidToObjectId(type.id),
            RegionId = MissingRegion,
          },
        },
      }, new TestContext()));

    Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);

    var storedDiagram = await db.Diagrams.AsNoTracking().SingleAsync(d => d.id == panel);
    Assert.Null(storedDiagram.dgr_type);
    var storedCopy = await db.Diagrams.AsNoTracking().SingleAsync(d => d.id == copy);
    Assert.Equal(OfferedRegion, storedCopy.region_id);
  }

  /// <summary>
  /// A type's own rows go with it — its regions and the part bindings that name them — by the
  /// database's cascade, and nothing else does. The in-memory provider has no cascades, so this is
  /// the only place the boundary can be checked; a deleted type must not reach another type's
  /// regions, another type's bindings or a diagram drawn from the other type.
  /// </summary>
  [Fact]
  public async Task DeletingAFreeTypeTakesItsChildrenAndNothingElse()
  {
    await using var connection = await MapPostgres.OpenAsync(fresh: true);
    if (connection is null)
      return;
    var db = connection.Db;

    var free = new DBDiagramType { id = Guid.NewGuid(), name = "spare panel" };
    free.regions.Add(TypeRegion(free.id, OfferedRegion));
    var live = new DBDiagramType { id = Guid.NewGuid(), name = PanelName };
    live.regions.Add(TypeRegion(live.id, OfferedRegion));
    db.DiagramTypes.AddRange(free, live);
    await db.SaveChangesAsync();

    db.DiagramTypeParts.Add(TypePart(free.id, "spare lamp"));
    db.DiagramTypeParts.Add(TypePart(live.id, "live lamp"));
    var drawn = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = drawn, name = PanelName });
    await db.SaveChangesAsync();

    // The diagram's own insert names the node, and only a save of the node has made that row
    // exist: EF has no relationship configured between the two, so the order is mine to keep.
    db.Diagrams.Add(new DBDiagram { id = drawn, dgr_type = live.id });
    await db.SaveChangesAsync();

    await new DiagramTypeService(db).DeleteAsync([Utils.ConvertGuidToObjectId(free.id)]);

    Assert.Null(await db.DiagramTypes.AsNoTracking()
      .FirstOrDefaultAsync(t => t.id == free.id));
    Assert.Empty(await db.DiagramTypeRegions.AsNoTracking()
      .Where(r => r.diagram_type_id == free.id).ToListAsync());
    Assert.Empty(await db.DiagramTypeParts.AsNoTracking()
      .Where(p => p.diagram_type_id == free.id).ToListAsync());

    Assert.NotNull(await db.DiagramTypes.AsNoTracking()
      .FirstOrDefaultAsync(t => t.id == live.id));
    Assert.Single(await db.DiagramTypeRegions.AsNoTracking()
      .Where(r => r.diagram_type_id == live.id).ToListAsync());
    Assert.Single(await db.DiagramTypeParts.AsNoTracking()
      .Where(p => p.diagram_type_id == live.id).ToListAsync());
    var diagram = await db.Diagrams.AsNoTracking().FirstOrDefaultAsync(d => d.id == drawn);
    Assert.NotNull(diagram);
    Assert.Equal(live.id, diagram!.dgr_type);
  }

  private static DBDiagramTypeRegion TypeRegion(Guid typeId, string key) => new()
  {
    diagram_type_id = typeId,
    region_key = key,
    geometry = new DBDiagramCoord { width = 0.5, height = 0.5 },
  };

  private static DBDiagramTypePart TypePart(Guid typeId, string token) => new()
  {
    diagram_type_id = typeId,
    region_key = OfferedRegion,
    part_kind = DeviceObjectId.RelayPart,
    match_by = DiagramPartMatch.Token,
    match_value = token,
  };

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
}
