using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Map;
using VmsDb;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.Domain.Arming;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The demo world's arming pass against a real database. InMemory cannot tell the two moments
// apart — it has no transactions — so what is checked here is the moment: a flag must never be
// derived from a world that is still uncommitted, and a world that fails mid-install must leave
// no flag of its own behind. Enabled by VMS_MAP_TEST_DSN, as the other Postgres tests are.
[Collection("postgis")]
public class DemoSeederPostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  // The fixtures these tests share.
  private const string Key = "umd-datacenter";
  private const string CameraName = "sim-master";
  private const string MapSpaceKey = "space/map";
  private const string RoomSpaceKey = "space/room";
  private const string MapSpaceName = "map";
  private const string RoomSpaceName = "room";
  private const string MissingSpaceKey = "space/missing";
  private const string LandmarkKey = "landmark/l1";
  private const string LandmarkName = "l1 red";

  private sealed class TestCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// A VmsCfg stand-in that records, for every write, where the camera's reference picture stood
  /// in the <b>committed</b> world at that moment: it reads the tree through a second connection,
  /// so a picture written inside the seeder's transaction is invisible to it. This is what tells
  /// "derived after the commit" from "derived before it" even when both compute the same value.
  /// </summary>
  private sealed class CommittedPlacementWriter(
    ICameraStateSource state, DbContextOptions<VmsMapDbContext> options) : ICfgArmingWriter
  {
    /// <summary>The space each write saw, by name; empty when there was no committed picture.</summary>
    public List<string> SpacesAtWrite { get; } = [];

    /// <summary>The flags the map wrote, as the real column reports a change.</summary>
    public List<(string CameraId, bool Armed)> Flags { get; } = [];

    public int Calls { get; private set; }

    public async Task<bool> SetCamArmedAsync(string cameraId, bool armed, CancellationToken ct)
    {
      Calls += 1;
      SpacesAtWrite.Add(await CommittedSpaceAsync(cameraId, ct));

      if (state is TestCameraState test && test.Armed == armed)
        return false;

      Flags.Add((cameraId, armed));
      if (state is TestCameraState test2)
        test2.Armed = armed;
      return true;
    }

    private async Task<string> CommittedSpaceAsync(string cameraId, CancellationToken ct)
    {
      await using var db = new VmsMapDbContext(options);

      var reference = await db.CameraPlacements.AsNoTracking()
        .Where(p => p.camera_id == cameraId && p.is_reference)
        .Select(p => (Guid?)p.object_id)
        .FirstOrDefaultAsync(ct);
      if (reference is null)
        return string.Empty;

      var space = await db.Markers.AsNoTracking()
        .Where(m => m.id == reference.Value)
        .Select(m => m.parent_id)
        .FirstOrDefaultAsync(ct);
      if (space is null)
        return string.Empty;

      return await db.Markers.AsNoTracking()
        .Where(m => m.id == space.Value)
        .Select(m => m.name)
        .FirstOrDefaultAsync(ct) ?? string.Empty;
    }
  }

  private sealed record Services(
    NpgsqlDataSource DataSource, VmsMapDbContext Db, DemoSeeder Seeder,
    TestCameraState CameraState, CommittedPlacementWriter Writer) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  private static DbContextOptions<VmsMapDbContext> OptionsOf(NpgsqlDataSource dataSource) =>
    new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(dataSource, o => o.UseNetTopologySuite())
      .Options;

  private static async Task<Services?> OpenAsync()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return null;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(migrationSource, "VmsMapTest", AppContext.BaseDirectory, "7743512881")
        .MigrateAsync();
    }

    var dataSourceBuilder = new NpgsqlDataSourceBuilder(Dsn);
    dataSourceBuilder.EnableDynamicJson();
    dataSourceBuilder.UseNetTopologySuite();
    var dataSource = dataSourceBuilder.Build();

    var options = OptionsOf(dataSource);
    var db = new VmsMapDbContext(options);
    await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");

    var map = new MapDb(db);
    var states = new StatesUpdateService(new StateService(db), map, db);
    var levels = new LevelService(db);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, new TrackService(db, levels), geo);
    var updates = new MapUpdateService(db, map, tracks, states);
    var catalog = new TestCatalog();
    var cameraState = new TestCameraState();
    var writer = new CommittedPlacementWriter(cameraState, options);
    var arming = new CameraArmingReconciler(
      db, TestWiring.Arming(db, cameraState), TestWiring.ObjectSettings(db), cameraState, writer,
      map, Microsoft.Extensions.Logging.Abstractions.NullLogger<CameraArmingReconciler>.Instance);
    var spatial = new SpatialDb(db, map, updates, geo, catalog, new TestNotifier(), arming);

    var seeder = new DemoSeeder(
      db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
      new RightService(db), geo, new FileService(db), new VisualService(db), spatial, catalog,
      TestWiring.DeviceNodes(db, map, updates), TestWiring.ObjectSettings(db),
      TestWiring.DeviceDiagrams(db, map, updates, geo));

    return new Services(dataSource, db, seeder, cameraState, writer);
  }

  private static DemoWorld World(
    int version, string spaceKey, IReadOnlyList<DemoLandmark>? landmarks = null) => new(
      Key,
      version,
      [],
      [],
      [
        new DemoSpace(MapSpaceKey, MapSpaceName, null, SpaceKind.Map,
          39.0, -77.0, 39.1, -76.9),
        new DemoSpace(RoomSpaceKey, RoomSpaceName, MapSpaceKey, SpaceKind.Plan,
          39.01, -76.99, 39.02, -76.98),
      ],
      [],
      [new DemoPlacement(CameraName, spaceKey, 39.015, -76.985)],
      [],
      [],
      landmarks ?? [],
      [],
      [],
      [],
      [],
      []);

  [Fact]
  public async Task TheInstallDerivesTheFlagOnlyFromTheCommittedWorld()
  {
    await using var s = await OpenAsync();
    if (s is null)
      return;

    // The world first puts the camera on the map; the rewrite moves it into the room.
    await s.Seeder.InstallAsync(World(1, MapSpaceKey), CancellationToken.None);

    s.Writer.SpacesAtWrite.Clear();
    s.Writer.Flags.Clear();
    s.CameraState.Armed = true; // as if a zone had put the camera on guard before the rewrite

    await s.Seeder.InstallAsync(World(2, RoomSpaceKey), CancellationToken.None);

    Assert.NotEmpty(s.Writer.SpacesAtWrite);
    Assert.All(s.Writer.SpacesAtWrite, space => Assert.Equal(RoomSpaceName, space));
    Assert.Contains((DemoIds.Camera(CameraName), false), s.Writer.Flags);
    Assert.False(s.CameraState.Armed);
  }

  [Fact]
  public async Task AFailedInstallWritesNoFlag()
  {
    await using var s = await OpenAsync();
    if (s is null)
      return;

    s.CameraState.Armed = true; // a write would be visible if the install derived before its commit
    var world = World(1, RoomSpaceKey,
      [new DemoLandmark(LandmarkKey, LandmarkName, MissingSpaceKey, 39.015, -76.985)]);

    await Assert.ThrowsAnyAsync<Exception>(
      () => s.Seeder.InstallAsync(world, CancellationToken.None));

    Assert.Empty(s.Writer.Flags);
    Assert.Equal(0, s.Writer.Calls);
  }
}
