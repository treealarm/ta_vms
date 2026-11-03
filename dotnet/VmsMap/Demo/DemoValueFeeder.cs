using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// Keeps the demo's rack readings alive: a temperature and a humidity per rack, written into the
  /// regions the Rack 42U type shows. ONVIF has no such readings — a camera reports a geolocation
  /// and relays, not a room's climate — so the demo makes its own until M2.3 brings real
  /// parameters (docs/plans/square-merge/m2.3-onvif-live-parameters.md).
  ///
  /// It writes only nodes the registry says the demo owns, and only while the world is installed:
  /// a node the administrator made is never touched and a removed world leaves nothing behind.
  public sealed class DemoValueFeeder
  {
    private static readonly string[] Feeds = ["temp", "humidity"];
    private static readonly Random Random = new();

    private readonly VmsMapDbContext _db;
    private readonly IValuesServiceInternal _values;

    public DemoValueFeeder(VmsMapDbContext db, IValuesServiceInternal values)
    {
      _db = db;
      _values = values;
    }

    /// One round: rewrite every demo rack's readings. Returns how many values were written.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
      var installed = await _db.DemoConfigs.AsNoTracking()
        .AnyAsync(c => c.demo_key == DemoWorlds.UmdDataCenterKey, ct);
      if (!installed) return 0;

      var owners = await _db.DemoObjects.AsNoTracking()
        .Where(o => o.demo_key == DemoWorlds.UmdDataCenterKey && o.logical_key.StartsWith("object/"))
        .Select(o => o.object_id)
        .ToListAsync(ct);
      if (owners.Count == 0) return 0;

      var diagrams = await _db.Diagrams.AsNoTracking()
        .Where(d => owners.Contains(d.id) && d.dgr_type != null)
        .ToListAsync(ct);
      if (diagrams.Count == 0) return 0;

      var typeIds = diagrams.Select(d => d.dgr_type!.Value).Distinct().ToList();
      var types = await _db.DiagramTypes.AsNoTracking()
        .Include(t => t.regions)
        .Where(t => typeIds.Contains(t.id))
        .ToListAsync(ct);

      // The regions a value can fill are the type's own: only a type that has them gets readings.
      var feeding = types
        .Where(t => t.regions.Any(region => Feeds.Contains(region.region_key)))
        .Select(t => t.id)
        .ToHashSet();

      var ownerIds = owners.Select(id => id.ToString()).ToList();
      var current = await _values.GetListByOwnersAsync(ownerIds);

      var values = new List<ValueDTO>();
      foreach (var diagram in diagrams.Where(d => feeding.Contains(d.dgr_type!.Value)))
      {
        var type = types.First(t => t.id == diagram.dgr_type!.Value);
        foreach (var region in type.regions.Where(r => Feeds.Contains(r.region_key)))
        {
          var id = DemoIds.Id($"value/{diagram.id}/{region.region_key}");
          current.TryGetValue(id, out var previous);

          values.Add(new ValueDTO
          {
            id = id,
            owner_id = diagram.id.ToString(),
            name = region.region_key,
            value = Next(region.region_key, previous?.value),
          });
        }
      }

      await _values.UpdateListAsync(values);
      return values.Count;
    }

    /// A drift around the previous reading rather than an unrelated number: a rack warming up a
    /// little and cooling down reads as a live room, not as a random one.
    private static double Next(string feed, object? previous)
    {
      var value = previous is double number ? number : Default(feed);
      var step = feed == "temp" ? 0.15 : 1.5;
      var next = value + (Random.NextDouble() * 2 - 1) * step;

      return feed == "temp"
        ? Math.Round(Math.Clamp(next, 19, 27), 1)
        : Math.Round(Math.Clamp(next, 30, 60), 1);
    }

    private static double Default(string feed) => feed == "temp" ? 22.5 : 45;
  }

  /// The feeder's clock: every five seconds, while the service runs. A removed world makes the
  /// round a no-op, so there is nothing to start and stop with the install.
  public sealed class DemoValueFeederService : BackgroundService
  {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DemoValueFeederService> _log;

    public DemoValueFeederService(IServiceScopeFactory scopes, ILogger<DemoValueFeederService> log)
    {
      _scopes = scopes;
      _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      using var timer = new PeriodicTimer(Interval);

      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        try
        {
          await using var scope = _scopes.CreateAsyncScope();
          await scope.ServiceProvider.GetRequiredService<DemoValueFeeder>().RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          return;
        }
        catch (Exception ex)
        {
          // A demo reading is never worth stopping the service for; the next tick tries again.
          _log.LogWarning(ex, "The demo value feeder failed");
        }
      }
    }
  }
}
