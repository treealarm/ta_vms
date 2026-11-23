using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The sweep is the safety net: it has to restore a state whose event was lost, and it must never
// overwrite a decision written by somebody with a fresher observation. An unchanged pass must not
// write at all — that is what keeps a hundred thousand cameras from opening a transaction each.
public sealed class CameraPresenceSweepTests
{
  private const string CameraId = TestCameras.One;
  private const string Name = "front door";
  private const string ServerId = "srv1";
  private const string MainStream = "main";

  private sealed class FakeVideo : ICameraVideoStateSource
  {
    public Dictionary<string, IReadOnlyDictionary<string, VideoChannelState>> States =
      new(StringComparer.Ordinal);

    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>> GetAsync(
      IReadOnlyCollection<CameraInfo> cameras, CancellationToken ct) =>
      Task.FromResult<IReadOnlyDictionary<string, IReadOnlyDictionary<string, VideoChannelState>>>(
        States);
  }

  private sealed class FakeProbe : ICameraReachabilityProbe
  {
    public CameraReachability Result { get; set; } = CameraReachability.Unreachable;

    public int Calls;

    public Task<CameraReachability> ProbeAsync(CameraInfo camera, CancellationToken ct)
    {
      Calls++;
      return Task.FromResult(Result);
    }
  }

  private sealed class FakeCatalog(List<CameraInfo> cameras) : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(cameras.FirstOrDefault(c => c.CameraId == cameraId));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(cameras.ToList());
  }

  private sealed class FakeServers : IMediaServerRegistry
  {
    public Task<string> FsForServerAsync(string serverId, CancellationToken ct) =>
      throw new NotSupportedException();

    public Task<string> OnvifForServerAsync(string serverId, CancellationToken ct) =>
      throw new NotSupportedException();

    public Task<string?> TryOnvifForServerAsync(string serverId, CancellationToken ct) =>
      Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> ServerIdsAsync(CancellationToken ct) =>
      Task.FromResult<IReadOnlyList<string>>([ServerId]);
  }

  private sealed record Fixture(
    VmsMapDbContext Db,
    CameraStateWriter Writer,
    FakeVideo Video,
    FakeProbe Probe,
    CameraPresenceSweep Sweep);

  private static Fixture Create(CameraInfo camera)
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var writer = new CameraStateWriter(new ObjectStateWriter(db, stateService, statesUpdate));
    var nodes = TestWiring.DeviceNodes(db, map, new MapUpdateService(
      db,
      map,
      new TracksUpdateService(map, new TrackService(db, new LevelService(db)),
        new GeoService(db, new LevelService(db))),
      statesUpdate));
    nodes.EnsureCameraAsync(camera, default).GetAwaiter().GetResult();

    var video = new FakeVideo();
    var probe = new FakeProbe();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton(db);
    services.AddSingleton(map);
    services.AddSingleton(stateService);
    services.AddSingleton<IStateService>(stateService);
    services.AddSingleton(statesUpdate);
    services.AddSingleton<IStatesUpdateService>(statesUpdate);
    services.AddSingleton<ObjectStateWriter>();
    services.AddScoped<CameraStateWriter>();
    services.AddScoped<CameraPresenceStore>();
    services.AddSingleton<ICameraVideoStateSource>(video);
    services.AddSingleton<ICameraReachabilityProbe>(probe);
    services.AddScoped<CameraPresenceService>();
    var provider = services.BuildServiceProvider();

    var sweep = new CameraPresenceSweep(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new FakeCatalog([camera]),
      new FakeServers(),
      NullLogger<CameraPresenceSweep>.Instance);

    return new Fixture(db, writer, video, probe, sweep);
  }

  private static CameraInfo Camera() =>
    new(CameraId, Name, ServerId, VideoStreams: [MainStream], HasOnvif: true);

  private static void Video(Fixture f, VideoChannelState state)
  {
    f.Video.States[CameraId] = new Dictionary<string, VideoChannelState>(StringComparer.Ordinal)
    {
      [MainStream] = state,
    };
  }

  private static Guid ObjectId => Guid.Parse(CameraObjectId.For(CameraId));

  private static async Task<string> State(Fixture f)
  {
    var row = await f.Db.ObjectStates.Include(s => s.states).SingleAsync(s => s.id == ObjectId);
    return string.Join(",", row.states.Select(s => s.state).OrderBy(s => s));
  }

  private static async Task SetWrittenAt(Fixture f, DateTime writtenAt)
  {
    var row = await f.Db.ObjectStates.SingleAsync(s => s.id == ObjectId);
    row.timestamp = writtenAt;
    await f.Db.SaveChangesAsync();
  }

  [Fact]
  public async Task ASweepRestoresAStateWhoseEventWasLost()
  {
    var f = Create(Camera());
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: false, default);
    Video(f, VideoChannelState.NoVideo);

    await f.Sweep.RunPassAsync(full: true, default);

    Assert.Equal(
      string.Join(",", new[] { CameraStateWriter.Disconnected, CameraStateWriter.NoVideo }.OrderBy(s => s)),
      await State(f));
  }

  [Fact]
  public async Task ASweepDoesNotOverwriteANewerDecision()
  {
    var f = Create(Camera());
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: false, default);
    // As if an event had written while this pass was reading: the row is newer than the pass's
    // observation, so its decision is the fresher one.
    await SetWrittenAt(f, DateTime.UtcNow.AddMinutes(5));
    Video(f, VideoChannelState.NoVideo);

    await f.Sweep.RunPassAsync(full: true, default);

    Assert.Equal(CameraStateWriter.Connected, await State(f));
  }

  [Fact]
  public async Task AnUnchangedSweepWritesNothing()
  {
    var f = Create(Camera());
    await f.Writer.ApplyPresenceAsync(CameraId, connected: true, noVideo: false, default);
    var writtenAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    await SetWrittenAt(f, writtenAt);
    Video(f, VideoChannelState.Alive);

    await f.Sweep.RunPassAsync(full: true, default);

    var row = await f.Db.ObjectStates.SingleAsync(s => s.id == ObjectId);
    Assert.Equal(writtenAt, row.timestamp);
    Assert.Equal(0, f.Probe.Calls);
  }
}
