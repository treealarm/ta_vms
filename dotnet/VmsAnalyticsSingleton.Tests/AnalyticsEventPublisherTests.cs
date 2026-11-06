using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace VmsAnalyticsSingleton.Tests;

// The row is the record: a detection stays unpublished until the bus took it, and the sweep is
// what makes a crash between the commit and the publish harmless.
public class AnalyticsEventPublisherTests
{
  private const string CameraId = "0e9d6f4a-3c1b-4a5f-9e2d-1b7c8a6f5e4d";

  private sealed class Factory(DbContextOptions<AnalyticsDbContext> options)
    : IDbContextFactory<AnalyticsDbContext>
  {
    public AnalyticsDbContext CreateDbContext() => new(options);

    public Task<AnalyticsDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
      Task.FromResult(CreateDbContext());
  }

  private sealed class RecordingSink : IAnalyticsEventSink
  {
    public List<AnalyticsEventPublisher.Message> Sent { get; } = [];

    public bool Fail { get; set; }

    public Task PublishAsync(AnalyticsEventPublisher.Message message, CancellationToken ct)
    {
      if (Fail)
        throw new InvalidOperationException("bus down");

      Sent.Add(message);
      return Task.CompletedTask;
    }
  }

  private static (AnalyticsEventPublisher Publisher, Factory Factory, RecordingSink Sink) Build()
  {
    var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;
    var factory = new Factory(options);
    var sink = new RecordingSink();
    return (new AnalyticsEventPublisher(factory, sink, NullLogger<AnalyticsEventPublisher>.Instance),
      factory, sink);
  }

  private static async Task<long> SeedAsync(Factory factory, TimeSpan age)
  {
    await using var db = factory.CreateDbContext();
    var row = new DbDetection
    {
      CameraId = CameraId,
      TrackId = 7,
      Kind = "person",
      Confidence = 0.9f,
      DetectedAt = DateTime.UtcNow - age,
      ObjectId = "obj-1",
    };
    db.Detections.Add(row);
    await db.SaveChangesAsync();
    return row.Id;
  }

  [Fact]
  public async Task ARecentRowIsSentAndMarked()
  {
    var (publisher, factory, sink) = Build();
    var id = await SeedAsync(factory, TimeSpan.FromSeconds(5));

    await publisher.SweepAsync(CancellationToken.None);

    var sent = Assert.Single(sink.Sent);
    Assert.Equal(id, sent.DetectionId);
    Assert.Equal(CameraId, sent.CameraId);
    Assert.Equal("person", sent.Kind);
    Assert.Equal("obj-1", sent.ObjectId);

    await using var db = factory.CreateDbContext();
    Assert.NotNull((await db.Detections.SingleAsync()).MapEventAt);
  }

  [Fact]
  public async Task ASecondSweepDoesNotSendAgain()
  {
    var (publisher, factory, sink) = Build();
    await SeedAsync(factory, TimeSpan.FromSeconds(5));

    await publisher.SweepAsync(CancellationToken.None);
    await publisher.SweepAsync(CancellationToken.None);

    Assert.Single(sink.Sent);
  }

  [Fact]
  public async Task ARowBeyondTheWindowIsGivenUpWithoutBeingSent()
  {
    var (publisher, factory, sink) = Build();
    await SeedAsync(factory, TimeSpan.FromHours(2));

    await publisher.SweepAsync(CancellationToken.None);

    Assert.Empty(sink.Sent);

    await using var db = factory.CreateDbContext();
    // Marked, so the partial index on the nulls does not carry the archive forever.
    Assert.NotNull((await db.Detections.SingleAsync()).MapEventAt);
  }

  [Fact]
  public async Task AFailedPublishLeavesTheRowForTheNextPass()
  {
    var (publisher, factory, sink) = Build();
    await SeedAsync(factory, TimeSpan.FromSeconds(5));

    sink.Fail = true;
    await publisher.SweepAsync(CancellationToken.None);
    Assert.Empty(sink.Sent);

    sink.Fail = false;
    await publisher.SweepAsync(CancellationToken.None);
    Assert.Single(sink.Sent);
  }
}
