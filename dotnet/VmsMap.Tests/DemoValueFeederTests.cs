using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The demo's own readings: written into the racks the registry names, never into a user's node,
// and taken away with the world.
public class DemoValueFeederTests
{
  private sealed class TestCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(null);

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }

  private sealed class TestNotifier : IMapConfigNotifier
  {
    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct) =>
      Task.CompletedTask;
  }

  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, DemoSeeder Seeder, DemoValueFeeder Feeder);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var states = new StatesUpdateService(new StateService(db), map, db);
    var levels = new LevelService(db);
    var geo = new GeoService(db, levels);
    var updates = new MapUpdateService(map, new TracksUpdateService(map, new TrackService(db, levels), geo), states);
    var values = new ValuesService(db);
    var spatial = new SpatialDb(db, map, updates, geo, new TestCatalog(), new TestNotifier());

    var seeder = new DemoSeeder(
      db, updates, new DiagramService(db), new DiagramTypeService(db), levels, states,
      new RightService(db), geo, new FileService(db), spatial, new TestCatalog());

    return new Services(db, map, seeder, new DemoValueFeeder(db, values));
  }

  private static async Task<Dictionary<string, ValueDTO>> ValuesOf(VmsMapDbContext db, Guid owner)
  {
    var rows = await db.Values.AsNoTracking().Where(v => v.owner_id == owner).ToListAsync();
    return rows.ToDictionary(row => row.name, row => ValuesService.ConvertDB2DTO(row));
  }

  [Fact]
  public async Task TheFeederWritesTemperatureAndHumidityIntoTheDemoRacks()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);

    var written = await s.Feeder.RunOnceAsync(CancellationToken.None);

    var racks = await s.Db.DemoObjects
      .Where(o => o.logical_key.StartsWith("object/rack-"))
      .Select(o => o.object_id)
      .ToListAsync();

    Assert.Equal(racks.Count * 2, written);
    foreach (var rack in racks)
    {
      var values = await ValuesOf(s.Db, rack);
      Assert.Equal(["humidity", "temp"], values.Keys.OrderBy(name => name, StringComparer.Ordinal));
      Assert.InRange(Convert.ToDouble(values["temp"].value), 19, 27);
      Assert.InRange(Convert.ToDouble(values["humidity"].value), 30, 60);
    }
  }

  [Fact]
  public async Task TheFeederLeavesAUsersNodeAloneEvenWithTheRackType()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);

    var user = new BaseMarkerDTO { id = Guid.NewGuid().ToString(), name = "The administrator's rack" };
    await s.Map.UpdateHierarchyAsync([user]);
    await new DiagramService(s.Db).UpdateListAsync([new DiagramDTO
    {
      id = user.id,
      dgr_type = DemoIds.Id("type/rack"),
      geometry = new DiagramCoordDTO { width = 200, height = 600 },
    }]);

    await s.Feeder.RunOnceAsync(CancellationToken.None);

    Assert.Empty(await s.Db.Values.Where(v => v.owner_id == Guid.Parse(user.id!)).ToListAsync());
  }

  [Fact]
  public async Task TheFeederWritesNothingWhileNoWorldIsInstalled()
  {
    var s = Create();

    Assert.Equal(0, await s.Feeder.RunOnceAsync(CancellationToken.None));
    Assert.Empty(await s.Db.Values.ToListAsync());
  }

  [Fact]
  public async Task RemovingTheWorldTakesItsReadingsWithIt()
  {
    var s = Create();
    await s.Seeder.InstallAsync(DemoWorlds.UmdDataCenter(), CancellationToken.None);
    await s.Feeder.RunOnceAsync(CancellationToken.None);
    Assert.NotEmpty(await s.Db.Values.ToListAsync());

    await s.Seeder.RemoveAsync(DemoWorlds.UmdDataCenterKey, CancellationToken.None);

    Assert.Empty(await s.Db.Values.ToListAsync());
    Assert.Equal(0, await s.Feeder.RunOnceAsync(CancellationToken.None));
  }
}
