using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VmsDb;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;

namespace VmsMap.Tests;

/// <summary>
/// The scratch Postgres database the concurrency tests share, and the services wired as the
/// application wires them. Enabled by <c>VMS_MAP_TEST_DSN</c>: without it every helper answers null
/// and the test returns, exactly as the other Postgres tests do. Two connections to the same
/// database are what two replicas of VmsMap look like, and the advisory locks are only observable
/// on a real provider.
/// </summary>
internal static class MapPostgres
{
  public static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  /// <summary>
  /// A data source and context of its own. The first call migrates and empties the shared scratch
  /// database; a second one joins it.
  /// </summary>
  public sealed record Connection(NpgsqlDataSource DataSource, VmsMapDbContext Db) : IAsyncDisposable
  {
    public async ValueTask DisposeAsync()
    {
      await Db.DisposeAsync();
      await DataSource.DisposeAsync();
    }
  }

  public static async Task<Connection?> OpenAsync(
    DbCommandInterceptor? interceptor = null, bool fresh = false)
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

    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(dataSource, o => o.UseNetTopologySuite());
    if (interceptor is not null)
      options = options.AddInterceptors(interceptor);

    var db = new VmsMapDbContext(options.Options);
    if (fresh)
    {
      // The templates are a dictionary of their own, not children of `objects`: a name left from an
      // earlier run would collide with this test's canvas type. `device_info` is a device registry
      // that no foreign key hangs off either, so it is emptied by name.
      await db.Database.ExecuteSqlRawAsync(
        "TRUNCATE objects, diagram_types, device_info CASCADE;");
    }

    return new Connection(dataSource, db);
  }

  /// <summary>The device diagram service as the application wires it: the scenario, with the reconciler as its step.</summary>
  public static DeviceDiagramService Diagrams(
    VmsMapDbContext db, ICameraCatalog? catalog = null, IDeviceSettingsSource? settings = null)
  {
    var (map, updates, geo) = Wire(db);
    return TestWiring.DeviceDiagrams(db, map, updates, geo, catalog, settings);
  }

  /// <summary>The tree's writer for one device: the camera's node, its parts, and the removal that takes them.</summary>
  public static DeviceNodeService Nodes(VmsMapDbContext db, ICameraCatalog catalog)
  {
    var (map, updates, _) = Wire(db);
    return new DeviceNodeService(
      db, map, updates, catalog, new NoDeviceSettings(),
      NullLogger<DeviceNodeService>.Instance);
  }

  private static (MapDb Map, MapUpdateService Updates, GeoService Geo) Wire(VmsMapDbContext db)
  {
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geo);
    return (map, new MapUpdateService(db, map, tracks, states), geo);
  }
}
