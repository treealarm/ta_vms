using System.Buffers.Binary;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.EntityFrameworkCore;
using Vms.Analytics;

namespace VmsAnalyticsSingleton.Tests;

public class SearchSimilarDetectionsTests
{
  [Fact]
  public async Task EmptyProbe_ReturnsInvalidArgument()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var service = CreateService(factory);
    var request = new SearchSimilarDetectionsRequest
    {
      FromTs = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(DateTime.UtcNow),
      Limit = 10,
    };

    var ex = await Assert.ThrowsAsync<RpcException>(() => service.SearchSimilarDetections(request, CreateContext()));
    Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
  }

  [Fact]
  public async Task DetectionProbe_FindsSimilarByEmbedding()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var probeId = await InsertDetectionAsync(db, "cam-a", 10, "person", DateTime.UtcNow.AddMinutes(-5), [1f, 0f]);
    var hitId = await InsertDetectionAsync(db, "cam-b", 20, "person", DateTime.UtcNow.AddMinutes(-4), [0.95f, 0.05f]);
    _ = await InsertDetectionAsync(db, "cam-c", 30, "person", DateTime.UtcNow.AddMinutes(-3), [-1f, 0f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      DetectionProbe = new DetectionIdProbe { DetectionId = probeId },
      FromTs = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(DateTime.UtcNow),
      MinScore = 0.7f,
      Limit = 10,
    }, CreateContext());

    Assert.Equal(2, response.Hits.Count);
    Assert.Contains(response.Hits, h => h.Detection.Id == probeId);
    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
    Assert.DoesNotContain(response.Hits, h => h.Detection.CameraId == "cam-c");
  }

  [Fact]
  public async Task DetectionProbe_UsesTrackFallback_WhenExactVectorMissing()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var anchorTs = DateTime.UtcNow.AddMinutes(-10);
    var probeId = await InsertDetectionAsync(db, "cam-a", 111, "person", anchorTs, null);
    _ = await InsertDetectionAsync(db, "cam-a", 111, "person", anchorTs.AddSeconds(2), [1f, 0f]);
    var hitId = await InsertDetectionAsync(db, "cam-z", 999, "person", anchorTs.AddMinutes(1), [0.9f, 0.1f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      DetectionProbe = new DetectionIdProbe { DetectionId = probeId },
      FromTs = Timestamp.FromDateTime(anchorTs.AddMinutes(-5)),
      ToTs = Timestamp.FromDateTime(anchorTs.AddMinutes(30)),
      MinScore = 0.6f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
  }

  [Fact]
  public async Task ArchiveProbe_FindsNearestTrackEmbedding()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var baseTs = DateTime.UtcNow.AddMinutes(-20);
    _ = await InsertDetectionAsync(db, "cam-a", 42, "person", baseTs.AddSeconds(-3), [1f, 0f]);
    _ = await InsertDetectionAsync(db, "cam-a", 42, "person", baseTs.AddSeconds(8), [0.98f, 0.02f]);
    var hitId = await InsertDetectionAsync(db, "cam-b", 43, "person", baseTs.AddMinutes(1), [0.9f, 0.1f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      ArchiveProbe = new ArchiveTrackProbe
      {
        CameraId = "cam-a",
        TrackId = 42,
        Ts = Timestamp.FromDateTime(baseTs),
        Kind = DetectionKind.Person,
      },
      FromTs = Timestamp.FromDateTime(baseTs.AddMinutes(-10)),
      ToTs = Timestamp.FromDateTime(baseTs.AddMinutes(30)),
      MinScore = 0.6f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
  }

  [Fact]
  public async Task LiveArchiveProbe_UsesRecentVectorBeforeItHasADetectionRow()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var now = DateTime.UtcNow;
    var hitId = await InsertDetectionAsync(db, "cam-b", 43, "person", now.AddMinutes(-1), [0.9f, 0.1f]);
    await db.SaveChangesAsync();

    var recent = new RecentLiveEmbeddingCache();
    recent.Offer("cam-live", 7, "person", now, [1f, 0f], model: null);
    var service = new AnalyticsServiceImpl(
      factory,
      new AnalyticsActivityLog(),
      new LiveDetectionBroadcaster(),
      new LiveEventBroadcaster(),
      null!,
      null!,
      workers: null,
      recentEmbeddings: recent);

    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      ArchiveProbe = new ArchiveTrackProbe
      {
        CameraId = "cam-live",
        TrackId = 7,
        Ts = Timestamp.FromDateTime(now),
        Kind = DetectionKind.Person,
      },
      FromTs = Timestamp.FromDateTime(now.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(now.AddMinutes(1)),
      MinScore = 0.6f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
  }

  [Fact]
  public async Task DetectionProbe_ComparesOnlyEmbeddingsOfItsOwnModel()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var now = DateTime.UtcNow;
    var probeId = await InsertDetectionAsync(db, "cam-a", 10, "person", now.AddMinutes(-5), [1f, 0f], "reid-new");
    var hitId = await InsertDetectionAsync(db, "cam-b", 20, "person", now.AddMinutes(-4), [0.95f, 0.05f], "reid-new");
    // The same vector from another model, and from a row written before models were recorded: as
    // close as it gets numerically, and still not comparable.
    var otherId = await InsertDetectionAsync(db, "cam-c", 30, "person", now.AddMinutes(-3), [1f, 0f], "reid-old");
    var legacyId = await InsertDetectionAsync(db, "cam-d", 40, "person", now.AddMinutes(-2), [1f, 0f], model: null);
    await db.SaveChangesAsync();

    var response = await CreateService(factory).SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      DetectionProbe = new DetectionIdProbe { DetectionId = probeId },
      FromTs = Timestamp.FromDateTime(now.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(now),
      MinScore = 0.7f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
    Assert.DoesNotContain(response.Hits, h => h.Detection.Id == otherId || h.Detection.Id == legacyId);
    Assert.Equal(2u, response.OtherModelCandidates);
  }

  [Fact]
  public void Gallery_LinksSightingsOnlyWithinOneModel()
  {
    Environment.SetEnvironmentVariable("ANALYTICS_REID_MATCH_MIN_SCORE", "0.5");
    Environment.SetEnvironmentVariable("ANALYTICS_REID_GALLERY_WINDOW_SECONDS", "600");
    var gallery = new EphemeralReidGallery(CreateFactory(),
      Microsoft.Extensions.Logging.Abstractions.NullLogger<EphemeralReidGallery>.Instance);

    float[] look = [1f, 0f];
    var first = gallery.Resolve(Analytics.DetectionKind.Person, look, "reid-a", true, "cam-a", 1, null);
    var otherModel = gallery.Resolve(Analytics.DetectionKind.Person, look, "reid-b", true, "cam-b", 2, null);
    var sameModel = gallery.Resolve(Analytics.DetectionKind.Person, look, "reid-a", true, "cam-c", 3, null);

    Assert.NotEqual(first, otherModel);
    Assert.Equal(first, sameModel);
  }

  [Fact]
  public async Task DetectionProbe_WithoutAnyEmbedding_ReturnsFailedPrecondition()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var probeId = await InsertDetectionAsync(db, "cam-a", 10, "person", DateTime.UtcNow.AddMinutes(-5), null);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var ex = await Assert.ThrowsAsync<RpcException>(() => service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      DetectionProbe = new DetectionIdProbe { DetectionId = probeId },
      FromTs = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(DateTime.UtcNow),
      Limit = 10,
    }, CreateContext()));

    Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
  }

  [Fact]
  public async Task ImageProbe_ReturnsUnavailable_UntilEmbedWorkersImplemented()
  {
    var factory = CreateFactory();
    var service = CreateService(factory);

    var ex = await Assert.ThrowsAsync<RpcException>(() => service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      ImageProbe = new ImageProbe
      {
        Kind = DetectionKind.Person,
        Jpeg = Google.Protobuf.ByteString.CopyFrom([1, 2, 3]),
      },
      FromTs = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(DateTime.UtcNow),
      Limit = 10,
    }, CreateContext()));

    Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
  }

  [Fact]
  public void DiversifyHitsByCamera_InterleavesCameras_WhenOneCameraDominatesScore()
  {
    var hits = new List<(DbDetection Row, float Score)>();
    for (var i = 0; i < 50; i++)
    {
      hits.Add((new DbDetection { Id = i, CameraId = "cam-a", DetectedAt = DateTime.UtcNow }, 0.99f - i * 0.001f));
    }
    hits.Add((new DbDetection { Id = 100, CameraId = "cam-b", DetectedAt = DateTime.UtcNow }, 0.75f));
    hits.Add((new DbDetection { Id = 101, CameraId = "cam-b", DetectedAt = DateTime.UtcNow }, 0.74f));

    // Global top-10 by score would be all cam-a (0.99..); diversification must surface cam-b.
    var selected = AnalyticsServiceImpl.DiversifyHitsByCamera(hits, limit: 10);

    Assert.Equal(10, selected.Count);
    Assert.Contains(selected, h => h.Row.CameraId == "cam-b");
    Assert.Equal(8, selected.Count(h => h.Row.CameraId == "cam-a"));
    Assert.Equal(2, selected.Count(h => h.Row.CameraId == "cam-b"));
  }

  [Fact]
  public async Task SearchSimilar_IncludesOtherCamera_WhenSameCameraHasDenserHighScores()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var now = DateTime.UtcNow;
    var probeId = await InsertDetectionAsync(db, "cam-a", 1, "person", now.AddMinutes(-5), [1f, 0f]);
    // Dense near-duplicates on the probe camera — without diversification these fill Limit alone.
    for (var i = 0; i < 30; i++)
    {
      var angle = i * 0.002f;
      _ = await InsertDetectionAsync(db, "cam-a", 1, "person", now.AddMinutes(-4).AddSeconds(i),
        [MathF.Sqrt(1f - angle * angle), angle]);
    }
    var otherId = await InsertDetectionAsync(db, "cam-b", 9, "person", now.AddMinutes(-3), [0.8f, 0.6f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      DetectionProbe = new DetectionIdProbe { DetectionId = probeId },
      FromTs = Timestamp.FromDateTime(now.AddHours(-1)),
      ToTs = Timestamp.FromDateTime(now.AddMinutes(1)),
      MinScore = 0.5f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == otherId);
    Assert.Contains(response.Hits, h => h.Detection.CameraId == "cam-b");
  }

  [Fact]
  public async Task ArchiveProbe_PersonScoresAgainstAllRecentTrackEmbeddings_NotOnlyNearest()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var click = DateTime.UtcNow.AddMinutes(-10);
    // Nearest-in-time sample is orthogonal to the true match; a body 8 s later on the same
    // track is the one that actually looks like the other camera.
    _ = await InsertDetectionAsync(db, "cam-a", 12, "person", click.AddSeconds(-1), [1f, 0f]);
    _ = await InsertDetectionAsync(db, "cam-a", 12, "person", click.AddSeconds(8), [0f, 1f]);
    var hitId = await InsertDetectionAsync(db, "cam-b", 29, "person", click.AddSeconds(7), [0f, 1f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      ArchiveProbe = new ArchiveTrackProbe
      {
        CameraId = "cam-a",
        TrackId = 12,
        Ts = Timestamp.FromDateTime(click),
        Kind = DetectionKind.Person,
      },
      FromTs = Timestamp.FromDateTime(click.AddMinutes(-5)),
      ToTs = Timestamp.FromDateTime(click.AddMinutes(5)),
      MinScore = 0.7f,
      Limit = 10,
    }, CreateContext());

    Assert.Contains(response.Hits, h => h.Detection.Id == hitId);
  }

  [Fact]
  public async Task ArchiveProbe_FaceDoesNotSearchALaterInconsistentIdentityOnTheSameTrack()
  {
    var factory = CreateFactory();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    var click = DateTime.UtcNow.AddMinutes(-10);
    // Clicked crop [1,0]; 15 s later the person-track picked up a different face [0,1].
    _ = await InsertDetectionAsync(db, "cam-a", 6, "face", click.AddSeconds(-6), [1f, 0f]);
    _ = await InsertDetectionAsync(db, "cam-a", 6, "face", click.AddSeconds(15), [0f, 1f]);
    var otherId = await InsertDetectionAsync(db, "cam-b", 1, "face", click.AddSeconds(7), [0f, 1f]);
    await db.SaveChangesAsync();

    var service = CreateService(factory);
    var response = await service.SearchSimilarDetections(new SearchSimilarDetectionsRequest
    {
      ArchiveProbe = new ArchiveTrackProbe
      {
        CameraId = "cam-a",
        TrackId = 6,
        Ts = Timestamp.FromDateTime(click),
        Kind = DetectionKind.Face,
      },
      FromTs = Timestamp.FromDateTime(click.AddMinutes(-5)),
      ToTs = Timestamp.FromDateTime(click.AddMinutes(5)),
      MinScore = 0.7f,
      Limit = 10,
    }, CreateContext());

    Assert.DoesNotContain(response.Hits, h => h.Detection.Id == otherId);
  }

  [Fact]
  public void SelectFaceProbeVectors_KeepsOnlySamplesSimilarToTheNearest()
  {
    float[] nearest = [1f, 0f];
    float[] same = [0.98f, 0.2f];
    float[] other = [0f, 1f];
    var selected = AnalyticsServiceImpl.SelectFaceProbeVectors([nearest, same, other]);
    Assert.Equal(2, selected.Count);
    Assert.Same(nearest, selected[0]);
    Assert.Same(same, selected[1]);
  }

  private static AnalyticsServiceImpl CreateService(IDbContextFactory<AnalyticsDbContext> factory)
    => new(factory, new AnalyticsActivityLog(), new LiveDetectionBroadcaster(), new LiveEventBroadcaster(), null!, null!);

  private static TestDbFactory CreateFactory()
  {
    var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;
    return new TestDbFactory(options);
  }

  private static ServerCallContext CreateContext()
    => TestServerCallContext.Create(
      method: "test",
      host: "localhost",
      deadline: DateTime.UtcNow.AddMinutes(1),
      requestHeaders: new Metadata(),
      cancellationToken: CancellationToken.None,
      peer: "peer",
      authContext: null,
      contextPropagationToken: null,
      writeHeadersFunc: _ => Task.CompletedTask,
      writeOptionsGetter: () => new WriteOptions(),
      writeOptionsSetter: _ => { });

  private static async Task<long> InsertDetectionAsync(
    AnalyticsDbContext db,
    string cameraId,
    long trackId,
    string kind,
    DateTime detectedAtUtc,
    float[]? embedding,
    string? model = null)
  {
    var row = new DbDetection
    {
      CameraId = cameraId,
      TrackId = trackId,
      Kind = kind,
      Confidence = 0.9f,
      DetectedAt = DateTime.SpecifyKind(detectedAtUtc, DateTimeKind.Utc),
      EventId = "event",
      EventUuid = Guid.NewGuid(),
      ObjectId = "obj-1",
    };
    db.Detections.Add(row);
    await db.SaveChangesAsync();

    if (embedding is not null)
    {
      db.DetectionEmbeddings.Add(new DbDetectionEmbedding
      {
        DetectionId = row.Id,
        Kind = kind,
        Dim = embedding.Length,
        Embedding = EncodeNormalized(embedding),
        Model = model,
      });
    }

    return row.Id;
  }

  private static byte[] EncodeNormalized(IReadOnlyList<float> source)
  {
    var normSq = 0.0;
    for (var i = 0; i < source.Count; i++)
      normSq += source[i] * source[i];
    var norm = (float)Math.Sqrt(normSq);
    var bytes = new byte[source.Count * sizeof(float)];
    for (var i = 0; i < source.Count; i++)
      BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float), sizeof(float)), source[i] / norm);
    return bytes;
  }

  private sealed class TestDbFactory(DbContextOptions<AnalyticsDbContext> options)
    : IDbContextFactory<AnalyticsDbContext>
  {
    public AnalyticsDbContext CreateDbContext() => new(options);
    public Task<AnalyticsDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
      => Task.FromResult(new AnalyticsDbContext(options));
  }
}
