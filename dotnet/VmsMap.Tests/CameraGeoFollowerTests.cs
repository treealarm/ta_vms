using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;

namespace VmsMap.Tests;

// The follower is the only thing that writes a camera's point from outside an administrator's
// action, so two properties matter: which cameras it asks at all, and when a written point is news
// rather than the same coordinate a still camera keeps answering.
public class CameraGeoFollowerTests
{
  private const string CameraId = "cam1";

  // About 1 m of latitude here; a degree is ~111 km, which the follower's equirectangular frame
  // takes as 2πR/360.
  private const double OneMetreInLatitude = 1.0 / 111_319.5;

  private sealed class FakeCatalog(params CameraInfo[] cameras) : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(cameras.FirstOrDefault(c => c.CameraId == cameraId));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(cameras.ToList());
  }

  private sealed class FakeSource : ICameraGeoSource
  {
    public Dictionary<string, CameraGeoPoint> Points { get; } = new(StringComparer.Ordinal);

    public List<string> Asked { get; } = [];

    public Task<CameraGeoPoint?> GetAsync(CameraInfo camera, CancellationToken ct)
    {
      Asked.Add(camera.CameraId);
      return Task.FromResult(
        Points.TryGetValue(camera.CameraId, out var point) ? point : (CameraGeoPoint?)null);
    }
  }

  private sealed class FakeWriter : ICameraGeoWriter
  {
    public List<(string CameraId, double Lat, double Lon)> Moves { get; } = [];

    public int Attempts { get; private set; }

    /// <summary>Cameras with no reference placement: the write is refused, and nothing is learned.</summary>
    public HashSet<string> WithoutPlacement { get; } = new(StringComparer.Ordinal);

    public Task<bool> MoveReferencePointAsync(
      string cameraId, double lat, double lon, CancellationToken ct)
    {
      Attempts++;
      if (WithoutPlacement.Contains(cameraId))
        return Task.FromResult(false);

      Moves.Add((cameraId, lat, lon));
      return Task.FromResult(true);
    }
  }

  private static (CameraGeoFollower Follower, FakeSource Source, FakeWriter Writer) Build(
    params CameraInfo[] cameras)
  {
    var source = new FakeSource();
    var writer = new FakeWriter();
    var provider = new ServiceCollection()
      .AddScoped<ICameraGeoWriter>(_ => writer)
      .BuildServiceProvider();
    var follower = new CameraGeoFollower(
      provider.GetRequiredService<IServiceScopeFactory>(),
      new FakeCatalog(cameras),
      source,
      NullLogger<CameraGeoFollower>.Instance);
    return (follower, source, writer);
  }

  private static CameraInfo Flagged(string cameraId = CameraId) =>
    new(cameraId, "Front door", "srv1", GeoFromDevice: true);

  private static CameraInfo Manual(string cameraId = CameraId) =>
    new(cameraId, "Front door", "srv1");

  [Fact]
  public async Task AFlaggedCameraAtANewPlaceMovesItsPoint()
  {
    var (follower, source, writer) = Build(Flagged());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);

    await follower.PollOnceAsync(CancellationToken.None);

    var move = Assert.Single(writer.Moves);
    Assert.Equal(CameraId, move.CameraId);
    Assert.Equal(38.9923, move.Lat, 6);
    Assert.Equal(-76.9378, move.Lon, 6);
  }

  [Fact]
  public async Task AStillCameraIsWrittenOnceAndThenLeftAlone()
  {
    var (follower, source, writer) = Build(Flagged());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);

    await follower.PollOnceAsync(CancellationToken.None);
    await follower.PollOnceAsync(CancellationToken.None);
    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Single(writer.Moves);
    // Asked every tick regardless: a camera can start moving between any two of them.
    Assert.Equal(3, source.Asked.Count);
  }

  [Fact]
  public async Task AMoveBelowTheThresholdIsNotWritten()
  {
    var (follower, source, writer) = Build(Flagged());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);
    await follower.PollOnceAsync(CancellationToken.None);

    // A third of a metre north: smaller than the threshold, so the map keeps the old point.
    source.Points[CameraId] = new CameraGeoPoint(38.9923 + OneMetreInLatitude / 3, -76.9378);
    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Single(writer.Moves);
  }

  [Fact]
  public async Task AMoveBeyondTheThresholdIsWritten()
  {
    var (follower, source, writer) = Build(Flagged());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);
    await follower.PollOnceAsync(CancellationToken.None);

    source.Points[CameraId] = new CameraGeoPoint(38.9923 + OneMetreInLatitude, -76.9378);
    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Equal(2, writer.Moves.Count);
    Assert.Equal(38.9923 + OneMetreInLatitude, writer.Moves[1].Lat, 9);
  }

  [Fact]
  public async Task ACameraWithoutTheFlagIsNeverAsked()
  {
    var (follower, source, writer) = Build(Manual());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);

    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Empty(source.Asked);
    Assert.Empty(writer.Moves);
  }

  [Fact]
  public async Task ACameraWithNoPlacementIsTriedAgainOnTheNextTick()
  {
    var (follower, source, writer) = Build(Flagged());
    source.Points[CameraId] = new CameraGeoPoint(38.9923, -76.9378);
    writer.WithoutPlacement.Add(CameraId);

    await follower.PollOnceAsync(CancellationToken.None);
    Assert.Empty(writer.Moves);

    // Nothing was remembered from the refused write: one placement appearing later is enough for
    // the camera to be put where it is, without waiting for it to move first.
    writer.WithoutPlacement.Remove(CameraId);
    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Single(writer.Moves);
    Assert.Equal(2, writer.Attempts);
  }

  [Fact]
  public async Task ACameraThatReportsNoPlaceIsNotWritten()
  {
    var (follower, _, writer) = Build(Flagged());

    await follower.PollOnceAsync(CancellationToken.None);

    Assert.Empty(writer.Moves);
  }
}
