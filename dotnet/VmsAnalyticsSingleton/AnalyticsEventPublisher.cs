using System.Text.Json;
using System.Threading.Channels;
using Dapr.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace VmsAnalyticsSingleton;

/// <summary>
/// Hands a persisted detection to the map as <c>analytics-event</c>.
/// </summary>
/// <remarks>
/// The detection row is itself the record that survives a crash: <c>map_event_at</c> is written
/// with the detection, in the same transaction, and stays null until the map has been told. The
/// live path is a channel fed right after the commit; a sweep every two seconds is the safety net
/// for a process that died between the commit and the publish, and for anything the bounded
/// channel dropped. Delivery is at-least-once — VmsMap deduplicates by the event id it derives
/// from <c>detection_id</c>.
/// <para>
/// The sweep gives up after an hour: an old unpublished row is marked as handed over without
/// being sent, because a reaction that late is a late reaction. That keeps the partial index on
/// the nulls proportional to the backlog rather than to the archive.
/// </para>
/// </remarks>
public sealed class AnalyticsEventPublisher : BackgroundService
{
  public const string Topic = "analytics-event";

  /// <summary>One persisted detection, in the shape VmsMap reads. One message per detection.</summary>
  public sealed record Message(
    long DetectionId,
    string CameraId,
    string Kind,
    float Confidence,
    string? ObjectId,
    DateTime DetectedAt);

  private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(1);
  private const int BatchSize = 200;

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private readonly IDbContextFactory<AnalyticsDbContext> _dbFactory;
  private readonly IAnalyticsEventSink _sink;
  private readonly ILogger<AnalyticsEventPublisher> _logger;

  // Bounded and drop-oldest, like the live overlay's fan-out: a slow bus must not make the
  // detection consumer wait, and the sweep covers whatever was dropped.
  private readonly Channel<long> _offered = Channel.CreateBounded<long>(new BoundedChannelOptions(1024)
  {
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
    SingleWriter = false,
  });

  public AnalyticsEventPublisher(
    IDbContextFactory<AnalyticsDbContext> dbFactory,
    IAnalyticsEventSink sink,
    ILogger<AnalyticsEventPublisher> logger)
  {
    _dbFactory = dbFactory;
    _sink = sink;
    _logger = logger;
  }

  /// <summary>Asks for a detection to be published now rather than at the next sweep.</summary>
  public void Offer(long detectionId) => _offered.Writer.TryWrite(detectionId);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(SweepInterval);

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        var read = _offered.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var completed = await Task.WhenAny(read, tick);

        if (completed == read && await read)
          await PublishAsync(Drain(), stoppingToken);
        else if (completed == tick && await tick)
          await SweepAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        // The rows stay unpublished and the next pass tries again.
        _logger.LogWarning(ex, "Publishing analytics events failed; the rows stay for the next pass");
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
      }
    }
  }

  private List<long> Drain()
  {
    var ids = new List<long>(BatchSize);
    while (ids.Count < BatchSize && _offered.Reader.TryRead(out var id))
      ids.Add(id);
    return ids;
  }

  internal async Task SweepAsync(CancellationToken ct)
  {
    await using var db = await _dbFactory.CreateDbContextAsync(ct);

    // A reaction that late is a late reaction: mark the backlog beyond the window as handed over
    // so it never returns to this query. Loaded and saved rather than a bulk UPDATE so the same
    // path works on the in-memory context the tests use.
    var stale = await db.Detections
      .Where(d => d.MapEventAt == null && d.DetectedAt < DateTime.UtcNow - GiveUpAfter)
      .ToListAsync(ct);
    foreach (var row in stale)
      row.MapEventAt = DateTime.UtcNow;
    if (stale.Count > 0)
      await db.SaveChangesAsync(ct);

    var ids = await db.Detections
      .AsNoTracking()
      .Where(d => d.MapEventAt == null)
      .OrderBy(d => d.Id)
      .Take(BatchSize)
      .Select(d => d.Id)
      .ToListAsync(ct);

    if (ids.Count > 0)
      await PublishAsync(ids, ct);
  }

  private async Task PublishAsync(IReadOnlyList<long> ids, CancellationToken ct)
  {
    if (ids.Count == 0)
      return;

    await using var db = await _dbFactory.CreateDbContextAsync(ct);
    var rows = await db.Detections
      .AsNoTracking()
      .Where(d => ids.Contains(d.Id) && d.MapEventAt == null)
      .ToListAsync(ct);

    var published = new List<long>(rows.Count);
    foreach (var row in rows)
    {
      try
      {
        var message = new Message(
          row.Id, row.CameraId, row.Kind, row.Confidence, row.ObjectId, row.DetectedAt);
        await _sink.PublishAsync(message, ct);
        published.Add(row.Id);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        // Order is preserved: the unpublished remainder is retried by the next sweep.
        _logger.LogWarning(ex,
          "Publishing analytics event for detection {DetectionId} failed; it stays for the next pass",
          row.Id);
        break;
      }
    }

    if (published.Count == 0)
      return;

    // Marked after the bus accepted the message. A crash in between republishes the same id, which
    // VmsMap deduplicates.
    var sent = await db.Detections.Where(d => published.Contains(d.Id)).ToListAsync(ct);
    foreach (var row in sent)
      row.MapEventAt = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);
  }
}

/// <summary>
/// Where a published detection goes. An interface rather than <see cref="DaprClient"/> directly:
/// the contract this class promises — every row is offered at least once, and marked only after
/// the sink took it — is what the tests can hold it to.
/// </summary>
public interface IAnalyticsEventSink
{
  Task PublishAsync(AnalyticsEventPublisher.Message message, CancellationToken ct);
}

/// <summary>The bus: one Dapr pub/sub message per detection.</summary>
public sealed class DaprAnalyticsEventSink(DaprClient dapr, string pubSubName) : IAnalyticsEventSink
{
  public Task PublishAsync(AnalyticsEventPublisher.Message message, CancellationToken ct) =>
    dapr.PublishEventAsync(pubSubName, AnalyticsEventPublisher.Topic, message, ct);
}
