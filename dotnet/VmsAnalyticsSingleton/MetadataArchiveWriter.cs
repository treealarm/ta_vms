using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Vms.Fs;

namespace VmsAnalyticsSingleton;

/// <summary>One box as its producer saw it, on its way to the archive.</summary>
public sealed record MetadataSample(
  string CameraId,
  string StreamName,
  /// <summary>"cv" | "onvif" — kept apart all the way to the renderer.</summary>
  string Source,
  DateTime DetectedAt,
  long TrackId,
  string Kind,
  float Confidence,
  float BboxX,
  float BboxY,
  float BboxWidth,
  float BboxHeight,
  string? Label,
  string? PlateText);

/// <summary>
/// Records analytics geometry into the archive, next to the video it describes.
/// </summary>
/// <remarks>
/// <para>
/// This service is the single writer for both detection sources, because it is the one place they
/// already converge (CV through <see cref="DetectionStreamConsumer"/>, ONVIF through
/// <c>AnalyticsServiceImpl.PushOnvifDetections</c>). Splitting the write would mean two decimation
/// policies and two answers to "which clip does this instant belong to". The stream name on each
/// sample is the detector's; VmsFs attaches the geometry to covering clips of any stream on that
/// camera, so a cheap analytics sub-stream still paints boxes over the main-stream archive.
/// </para>
/// <para>
/// Best-effort by construction, exactly like the live overlay it shares a source with: the offer
/// channel drops its oldest entries rather than let a slow VmsFs back-pressure detection ingest,
/// and a failed send is dropped rather than retried. A retry would arrive at a clip that may have
/// been deleted in the meantime, and it would arrive behind everything queued after it.
/// </para>
/// <para>
/// On clocks: CV samples carry <c>DetectionEvent.detected_at</c>, which video_a stamps after
/// inference, on its own host, downstream of a MediaMTX relay hop — so a CV box in the archive can
/// sit a few hundred milliseconds behind the frame it describes. It cannot be corrected from this
/// repository (analytics.proto is a vendored copy of another repo's contract, and a computed offset
/// would be an invented number). The real fix belongs in video_a: stamp capture time and carry it
/// through the relay. ONVIF samples do not have this problem — video_srv stamps them from the
/// same wall clock that bounds clips — which is why ONVIF wins when the renderer has both.
/// </para>
/// </remarks>
public sealed class MetadataArchiveWriter : BackgroundService
{
  /// <summary>
  /// How long a frame waits before being sent. Segment rotation posts two IngestClip calls (close
  /// of the old segment, open of the new one); holding the geometry briefly lets both land, so
  /// VmsFs resolves against a settled picture instead of a boundary that is still moving. The
  /// archive can afford the delay — nothing is watching these boxes in real time.
  /// </summary>
  private static readonly TimeSpan IngestDelay = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Flush cadence, which is also the chunk size. A constant rather than a second environment
  /// variable: it shapes the rows, not the behaviour.
  /// </summary>
  private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

  private static readonly TimeSpan SendFailureLogInterval = TimeSpan.FromSeconds(30);

  private readonly Channel<MetadataSample> _channel = Channel.CreateBounded<MetadataSample>(
    new BoundedChannelOptions(4096)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
    });

  private readonly CameraServerCache _serverCache;
  private readonly ServerPairRegistry _serverPairRegistry;
  private readonly ILogger<MetadataArchiveWriter> _logger;
  private readonly int _maxSampleHz;

  private readonly ConcurrentDictionary<string, DateTime> _lastFailureLoggedAt = new();

  /// <summary>
  /// One sampling bucket: the boxes assembled for it, and when this process first saw it. The
  /// latter is what decides when the bucket is sent — see FlushAsync.
  /// </summary>
  private sealed class Bucket(DateTime firstSeenLocal)
  {
    public DateTime FirstSeenLocal { get; } = firstSeenLocal;
    public Dictionary<(long TrackId, string Kind), MetadataSample> Boxes { get; } = [];
  }

  public MetadataArchiveWriter(
    CameraServerCache serverCache,
    ServerPairRegistry serverPairRegistry,
    ILogger<MetadataArchiveWriter> logger)
  {
    _serverCache = serverCache;
    _serverPairRegistry = serverPairRegistry;
    _logger = logger;
    _maxSampleHz = ReadMaxSampleHz();
  }

  /// <summary>
  /// Samples per second kept per (camera, stream, source). 0 disables archiving of metadata
  /// entirely — a documented feature gate, so it is read here rather than defaulted anywhere.
  /// </summary>
  public static int ReadMaxSampleHz()
  {
    var raw = Environment.GetEnvironmentVariable("ANALYTICS_METADATA_MAX_SAMPLE_HZ")
      ?? throw new InvalidOperationException(
        "Required environment variable 'ANALYTICS_METADATA_MAX_SAMPLE_HZ' is not set");
    if (!int.TryParse(raw, out var hz) || hz < 0)
      throw new InvalidOperationException(
        $"Environment variable 'ANALYTICS_METADATA_MAX_SAMPLE_HZ' must be a non-negative integer, got '{raw}'");
    return hz;
  }

  /// <summary>Queues one box. Safe from any thread; never blocks, never throws.</summary>
  public void Offer(MetadataSample sample)
  {
    if (_maxSampleHz == 0)
      return;
    _channel.Writer.TryWrite(sample);
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    // Registered unconditionally and disabled here rather than left out of DI, so the callers stay
    // free of a nullable dependency. Offer() is already a no-op at this setting.
    if (_maxSampleHz == 0)
    {
      _logger.LogInformation(
        "ANALYTICS_METADATA_MAX_SAMPLE_HZ=0 — analytics geometry is not recorded into the archive");
      return;
    }

    // Buckets of decimated frames per (camera, stream, source), each keyed by the sampling bucket
    // index so a whole frame is assembled from boxes that arrive one at a time.
    var pending = new Dictionary<(string CameraId, string StreamName, string Source),
      SortedDictionary<long, Bucket>>();

    var bucketMs = 1000.0 / _maxSampleHz;

    while (!stoppingToken.IsCancellationRequested)
    {
      using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
      flushCts.CancelAfter(FlushInterval);

      try
      {
        while (await _channel.Reader.WaitToReadAsync(flushCts.Token))
        {
          while (_channel.Reader.TryRead(out var sample))
          {
            var key = (sample.CameraId, sample.StreamName, sample.Source);
            if (!pending.TryGetValue(key, out var buckets))
              pending[key] = buckets = [];

            var bucket = (long)Math.Floor(
              (sample.DetectedAt - DateTime.UnixEpoch).TotalMilliseconds / bucketMs);
            if (!buckets.TryGetValue(bucket, out var entry))
              buckets[bucket] = entry = new Bucket(DateTime.UtcNow);

            // Last box wins inside a bucket: the detector may report the same track several times
            // in one sampling window, and the freshest position is the one worth keeping.
            entry.Boxes[(sample.TrackId, sample.Kind)] = sample;
          }
        }
      }
      catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
      {
        // Flush interval elapsed — fall through.
      }
      catch (OperationCanceledException)
      {
        break;
      }

      await FlushAsync(pending, stoppingToken);
    }
  }

  private async Task FlushAsync(
    Dictionary<(string CameraId, string StreamName, string Source),
      SortedDictionary<long, Bucket>> pending,
    CancellationToken ct)
  {
    if (pending.Count == 0)
      return;

    // Ripeness is measured on this process's clock, from when the bucket was first filled here —
    // never by comparing a producer's timestamp to local time. CV samples are stamped by video_a on
    // another host and ONVIF ones by whichever video_srv saw them, so a producer clock running
    // even slightly ahead would leave every bucket permanently unripe: nothing would ever be
    // archived, and `pending` would grow without bound while looking perfectly healthy.
    var cutoff = DateTime.UtcNow - IngestDelay;
    var emptied = new List<(string, string, string)>();

    foreach (var (key, buckets) in pending)
    {
      var ripe = buckets
        .Where(b => b.Value.FirstSeenLocal <= cutoff)
        .ToList();
      if (ripe.Count > 0)
      {
        var request = new PushMetadataSamplesRequest
        {
          CameraId = key.CameraId,
          StreamName = key.StreamName,
          Source = key.Source,
        };
        foreach (var (bucket, entry) in ripe)
        {
          var boxes = entry.Boxes;
          // The frame's instant is the newest detection in the bucket, so a box never claims to be
          // older than the moment it was actually reported.
          var ts = boxes.Values.Max(s => s.DetectedAt);
          var frame = new MetadataFrame
          {
            Ts = Timestamp.FromDateTime(DateTime.SpecifyKind(ts, DateTimeKind.Utc)),
          };
          foreach (var s in boxes.Values)
          {
            var box = new MetadataBox
            {
              TrackId = s.TrackId,
              Kind = s.Kind,
              Confidence = s.Confidence,
              X = s.BboxX,
              Y = s.BboxY,
              Width = s.BboxWidth,
              Height = s.BboxHeight,
            };
            if (s.Label is not null) box.Label = s.Label;
            if (s.PlateText is not null) box.PlateText = s.PlateText;
            frame.Boxes.Add(box);
          }
          request.Frames.Add(frame);
          buckets.Remove(bucket);
        }

        await SendAsync(request, ct);
      }

      if (buckets.Count == 0)
        emptied.Add(key);
    }

    foreach (var key in emptied)
      pending.Remove(key);
  }

  private async Task SendAsync(PushMetadataSamplesRequest request, CancellationToken ct)
  {
    try
    {
      var serverId = await _serverCache.GetServerIdAsync(request.CameraId, ct);
      var fsClient = _serverPairRegistry.GetFsClient(serverId);
      await fsClient.PushMetadataSamplesAsync(request, cancellationToken: ct);
      _lastFailureLoggedAt.TryRemove(request.CameraId, out _);
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
      // Dropped, not retried — see the class remarks. Logged at most once per camera per interval
      // so an FS outage cannot drown the log.
      var now = DateTime.UtcNow;
      if (!_lastFailureLoggedAt.TryGetValue(request.CameraId, out var last)
          || now - last >= SendFailureLogInterval)
      {
        _lastFailureLoggedAt[request.CameraId] = now;
        _logger.LogWarning(ex,
          "Metadata samples dropped for camera '{CameraId}' ({Frames} frame(s))",
          request.CameraId, request.Frames.Count);
      }
    }
  }
}
