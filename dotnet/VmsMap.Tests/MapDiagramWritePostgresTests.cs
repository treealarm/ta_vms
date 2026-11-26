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
