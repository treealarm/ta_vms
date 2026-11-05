using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// Keeps the demo's camera readings alive: where the camera says it stands, how fast it is moving,
  /// and what its relay and sensor are doing, written into the regions of the Device telemetry
  /// diagram. The camera is asked through VmsOnvif — the same cache the web reads — so the demo
  /// shows what ONVIF actually reports rather than a number invented here.
  ///
  /// It writes only values of nodes the registry says the demo owns: the diagram is a replica, and
  /// its owner is the camera's canonical node, which is where the values live. A node the
  /// administrator made is never touched and a removed world leaves nothing behind.
  public sealed class DemoValueFeeder
  {
    private const string On = "on";
    private const string Off = "off";

    private readonly VmsMapDbContext _db;
    private readonly IValuesServiceInternal _values;
    private readonly ICameraCatalog _catalog;
    private readonly ICameraGeoSource _geo;
    private readonly ICameraDeviceIoSource _io;
    private readonly TimeProvider _time;

    // The last position and the moment it was read, for the speed: a moving camera reports only
    // places, and a speed is what two of them over a known interval make.
    private readonly Dictionary<string, (CameraGeoPoint Point, DateTimeOffset At)> _lastFix =
      new(StringComparer.Ordinal);

    public DemoValueFeeder(
      VmsMapDbContext db,
      IValuesServiceInternal values,
      ICameraCatalog catalog,
      ICameraGeoSource geo,
      ICameraDeviceIoSource io,
      TimeProvider time)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _values = values ?? throw new ArgumentNullException(nameof(values));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _geo = geo ?? throw new ArgumentNullException(nameof(geo));
      _io = io ?? throw new ArgumentNullException(nameof(io));
      _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// One round: rewrite every telemetry reading. Returns how many values were written.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
      var installed = await _db.DemoConfigs.AsNoTracking()
        .AnyAsync(c => c.demo_key == DemoWorlds.UmdDataCenterKey, ct);
      if (!installed) return 0;

      var registered = await _db.DemoObjects.AsNoTracking()
        .Where(o => o.demo_key == DemoWorlds.UmdDataCenterKey)
        .ToListAsync(ct);

      var objectIds = registered
        .Where(o => o.logical_key.StartsWith("object/", StringComparison.Ordinal))
        .Select(o => o.object_id)
        .ToList();
      if (objectIds.Count == 0) return 0;

      var diagrams = await _db.Diagrams.AsNoTracking()
        .Where(d => objectIds.Contains(d.id) && d.dgr_type != null)
        .ToListAsync(ct);
      if (diagrams.Count == 0) return 0;

      var typeIds = diagrams.Select(d => d.dgr_type!.Value).Distinct().ToList();
      var types = await _db.DiagramTypes.AsNoTracking()
        .Include(t => t.regions)
        .Where(t => typeIds.Contains(t.id))
        .ToListAsync(ct);

      // The regions a value can fill are the type's own: only a type that has them gets readings.
      var feeding = types
        .Where(t => t.regions.Any(region => DemoTelemetry.Readings.Contains(region.region_key)))
        .Select(t => t.id)
        .ToHashSet();

      var telemetryIds = diagrams
        .Where(d => feeding.Contains(d.dgr_type!.Value))
        .Select(d => d.id)
        .ToList();
      if (telemetryIds.Count == 0) return 0;

      // A replica owns nothing: the readings belong to the camera's canonical node, and the replica
      // shows them because the map answers a replica with its owner's values.
      var owners = await _db.Markers.AsNoTracking()
        .Where(m => telemetryIds.Contains(m.id) && m.owner_id != null)
        .Select(m => m.owner_id!.Value)
        .ToListAsync(ct);
      if (owners.Count == 0) return 0;

      // Every camera the demo placed is asked: the relay and the sensor may be on different ones —
      // that is the point of the wiring.
      var placedIds = registered
        .Where(o => o.logical_key.StartsWith("camera/", StringComparison.Ordinal))
        .Select(o => o.logical_key["camera/".Length..])
        .ToHashSet(StringComparer.Ordinal);
      var cameras = (await _catalog.ListAsync(null, ct))
        .Where(camera => placedIds.Contains(camera.CameraId))
        .ToList();

      var relay = await FirstAsync(cameras, _io.GetRelayAsync, ct);
      var sensor = await FirstAsync(cameras, _io.GetInputAsync, ct);

      var values = new List<ValueDTO>();
      foreach (var owner in owners)
      {
        var cameraId = await _db.CameraNodes.AsNoTracking()
          .Where(node => node.object_id == owner)
          .Select(node => node.camera_id)
          .FirstOrDefaultAsync(ct);
        if (cameraId is null) continue;

        var camera = cameras.FirstOrDefault(c => c.CameraId == cameraId)
          ?? await _catalog.GetAsync(cameraId, ct);
        if (camera is null) continue;

        var point = await _geo.GetAsync(camera, ct);
        if (point is not { } fix) continue; // no place this tick: nothing to write, retry next time

        var now = _time.GetUtcNow();
        double? speed = null;
        if (_lastFix.TryGetValue(cameraId, out var previous))
        {
          var seconds = (now - previous.At).TotalSeconds;
          if (seconds > 0)
            speed = previous.Point.DistanceMetersTo(fix) / seconds * 3.6;
        }
        _lastFix[cameraId] = (fix, now);

        var ownerId = owner.ToString();
        void Add(string name, string text) => values.Add(new ValueDTO
        {
          id = DemoIds.Id($"value/{ownerId}/{name}"),
          owner_id = ownerId,
          name = name,
          value = text,
        });

        // Text rather than numbers, because the reading's own precision is part of what it says: six
        // decimals of a degree, one of a kilometre per hour.
        Add(DemoTelemetry.Lat, fix.Lat.ToString("F6", CultureInfo.InvariantCulture));
        Add(DemoTelemetry.Lon, fix.Lon.ToString("F6", CultureInfo.InvariantCulture));
        if (speed is { } kmh)
          Add(DemoTelemetry.Speed, kmh.ToString("F1", CultureInfo.InvariantCulture));
        if (relay.Live is { } relayOn)
          Add(DemoTelemetry.Relay, relayOn ? On : Off);
        if (sensor.Live is { } sensorOn)
          Add(DemoTelemetry.Sensor, sensorOn ? On : Off);
      }

      if (values.Count == 0) return 0;
      await _values.UpdateListAsync(values);
      return values.Count;
    }

    /// The first camera that has such a device at all. A camera that has one but has not announced
    /// its state yet still answers, with a null state — the demo reports "no reading", not "off".
    private static async Task<CameraDeviceState> FirstAsync(
      IReadOnlyList<CameraInfo> cameras,
      Func<CameraInfo, CancellationToken, Task<CameraDeviceState>> read,
      CancellationToken ct)
    {
      foreach (var camera in cameras)
      {
        var state = await read(camera, ct);
        if (state.HasDevices)
          return state;
      }
      return new CameraDeviceState(false, null);
    }
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
