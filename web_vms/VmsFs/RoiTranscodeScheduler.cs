using Grpc.Core;
using Dapr.Client;
using Roi.Transcode;

namespace VmsFs;

/// <summary>
/// Re-encodes finished archive clips with ROI, keeping what the detector found
/// sharp and spending fewer bits on the rest, then replaces the clip with the
/// smaller file.
///
/// Lives here for the same reason EdgeImportScheduler does: this is the node that
/// owns the archive volume, and a clip can only be re-encoded by something that
/// can open it. Shaped after that scheduler too -- the ROI service's
/// StartFileJob/GetJob deliberately mirror StartEdgeImport/GetEdgeImportJob.
///
/// Does nothing unless a camera asks for it and the clip actually has boxes:
/// a clip with nothing worth protecting has nothing to gain and would only lose
/// a generation of quality.
/// </summary>
public sealed class RoiTranscodeScheduler(
  ClipStorage clips,
  MetadataSampleStore metadata,
  CameraSetupCache cameras,
  StorageListCache storages,
  ILogger<RoiTranscodeScheduler> logger) : IHostedService, IAsyncDisposable
{
  /// <summary>What one sweep did, which decides how long to wait before the next.</summary>
  private enum SweepResult
  {
    /// <summary>Nothing eligible, or nothing that can be acted on. Wait the full tick.</summary>
    Idle,
    /// <summary>The clip was ruled out and its verdict written. Cheap, so the
    /// sweep keeps going and the next one should follow promptly.</summary>
    Skipped,
    /// <summary>A clip was encoded, or tried and genuinely failed. There is likely more.</summary>
    Worked,
    /// <summary>The service could not be reached. Waiting is the only useful response.</summary>
    Unreachable,
  }

  private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
  // After real work, just enough of a pause to stay out of a tight loop. The
  // encode itself is the throttle: it occupies the machine for as long as it
  // takes, and sleeping a full tick on top of that halved the rate for nothing.
  private static readonly TimeSpan Breath = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(5);
  // Generous: a long clip on the CPU fallback is minutes of work, and giving up
  // early would leave the service encoding into a file nobody waits for.
  private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);
  // A camera may ask for no delay at all, but the clip still has to be closed and
  // its boxes written -- MetadataArchiveWriter holds samples briefly and flushes
  // in batches. This is a correctness floor, so it is not configurable.
  private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(60);
  private const long MaxRetrySeconds = 6 * 60 * 60;

  private static int EnvInt(string name, int fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
  }

  private static double EnvDouble(string name, double fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    // Zero is a legitimate setting here -- a coverage floor of 0 means "take
    // anything with a box" -- so only a negative or unparsable value falls back.
    return double.TryParse(raw, System.Globalization.NumberStyles.Float,
      System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
  }

  private static int EnvSigned(string name, int fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var v) ? v : fallback;
  }

  private readonly int _retrySeconds = EnvInt("VMS_ROI_RETRY_SECONDS", 600);
  // Must be the number the encoder is given too: coverage below is measured with
  // it, and a scheduler measuring one window while the encoder protects another
  // would put the thresholds on a lie. Tied to ANALYTICS_METADATA_MAX_SAMPLE_HZ:
  // below half the interval between samples, gaps open up between them.
  private readonly double _holdSec = EnvDouble("VMS_ROI_HOLD_SEC", 0.5);
  // The working range. Below the floor the detector barely saw anything and
  // crushing the rest would be taking its word for the whole clip; above the
  // ceiling there is little background left to crush, so the pass would spend a
  // generation of quality for almost nothing.
  private readonly int _maxAttempts = EnvInt("VMS_ROI_MAX_ATTEMPTS", 3);
  private readonly int _crf = EnvInt("VMS_ROI_CRF", 28);
  private readonly int _qpDelta = EnvSigned("VMS_ROI_QP_DELTA", -8);
  private readonly int _backgroundQpDelta = EnvSigned("VMS_ROI_BACKGROUND_QP_DELTA", 8);
  private readonly double _pad = EnvDouble("VMS_ROI_PAD", 0.05);

  private readonly CancellationTokenSource _cts = new();
  private Task? _loop;
  private TimeSpan _nextDelay = Tick;

  // Only this node's cameras. Until it arrives every tick is skipped: both VmsFs
  // instances share one database, and without the scope one would re-encode the
  // other's clips over a filesystem it cannot see.
  private volatile bool _scopeReady;
  public string? OwnServerId { get; private set; }

  public void SetScope(string? serverId)
  {
    OwnServerId = serverId;
    _scopeReady = true;
  }

  public Task StartAsync(CancellationToken ct)
  {
    var appId = Environment.GetEnvironmentVariable("DAPR_ROI_APP_ID");
    if (string.IsNullOrWhiteSpace(appId))
    {
      // Feature gate, not a missing setting: a deployment without a ROI service
      // simply does not re-compress, and saying so once is better than a
      // scheduler that wakes up forever with nowhere to send work.
      logger.LogInformation("ROI transcode: DAPR_ROI_APP_ID is not set, pass disabled");
      return Task.CompletedTask;
    }

    _loop = Task.Run(() => LoopAsync(appId, _cts.Token), CancellationToken.None);
    return Task.CompletedTask;
  }

  private async Task LoopAsync(string appId, CancellationToken ct)
  {
    var client = new RoiTranscode.RoiTranscodeClient(DaprClient.CreateInvocationInvoker(appId));

    while (!ct.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(_nextDelay, ct);
        if (!_scopeReady)
          continue;
        var result = await SweepAsync(client, ct);
        _nextDelay = result == SweepResult.Worked ? Breath : Tick;
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "ROI transcode: sweep failed");
      }
    }
  }

  private async Task<SweepResult> SweepAsync(
    RoiTranscode.RoiTranscodeClient client, CancellationToken ct)
  {
    var enabled = await EnabledCamerasAsync(ct);
    if (enabled.Count == 0)
      return SweepResult.Idle;

    // One candidate per camera, each with that camera's own cutoff, then the
    // oldest of them. Asking per camera keeps a camera with a long delay from
    // crowding out one with a short delay, and taking the oldest keeps the
    // backlog in order.
    var now = DateTime.UtcNow;
    var candidates = new List<(ClipStorage.RoiCandidate Clip, RoiCameraSettings Camera)>();
    foreach (var camera in enabled)
    {
      if (ct.IsCancellationRequested)
        return SweepResult.Idle;
      var candidate = await clips.GetOldestRoiCandidateAsync(
        camera.CameraId, now - DelayFor(camera.DelayHours), ct);
      if (candidate is not null)
        candidates.Add((candidate, camera));
    }

    var skipped = false;
    foreach (var (clip, camera) in candidates.OrderBy(c => c.Clip.EndTs))
    {
      if (ct.IsCancellationRequested)
        return SweepResult.Idle;
      // One clip at a time: re-encoding competes with recording for the same
      // machine, and on the CPU fallback it competes hard. The sweep ends as
      // soon as one clip is actually attempted.
      var result = await ProcessAsync(client, clip, camera, ct);
      if (result == SweepResult.Skipped)
      {
        skipped = true;
        continue;
      }
      if (result != SweepResult.Idle)
        return result;
    }
    // A sweep that only ruled clips out still made progress -- a camera with a
    // long run of clips below the coverage floor would otherwise drain at one
    // per tick.
    return skipped ? SweepResult.Worked : SweepResult.Idle;
  }

  /// <summary>
  /// What a camera says about how its clips should be re-compressed. The whole of
  /// it comes from the camera's own configuration: how long footage stays fresh and
  /// which clips are worth the trouble both follow from what the camera watches, so
  /// neither can be a node-wide setting.
  /// </summary>
  private sealed record RoiCameraSettings(
    string CameraId, double DelayHours, double MinCoverage, double MaxCoverage)
  {
    /// <summary>Coverage at or below which the clip is left alone. Never negative.</summary>
    public double Floor => MinCoverage > 0 ? MinCoverage : 0;

    /// <summary>
    /// Coverage above which the clip is left alone. 0 means no ceiling -- a camera
    /// nobody has configured must not be silently excluded from the pass.
    /// </summary>
    public double Ceiling => MaxCoverage > 0 ? MaxCoverage : 1;
  }

  /// <summary>Cameras on this node whose owner asked for ROI re-compression.</summary>
  private async Task<List<RoiCameraSettings>> EnabledCamerasAsync(
    CancellationToken ct)
  {
    var ids = await clips.GetRoiCameraIdsAsync(ct);
    var enabled = new List<RoiCameraSettings>();
    foreach (var cameraId in ids)
    {
      var setup = await cameras.GetAsync(cameraId, ct);
      if (setup is null || !setup.RoiTranscode)
        continue;
      if (!string.IsNullOrEmpty(OwnServerId)
        && !string.Equals(setup.ServerId, OwnServerId, StringComparison.OrdinalIgnoreCase))
        continue;
      enabled.Add(new RoiCameraSettings(
        cameraId, setup.RoiDelayHours, setup.RoiMinCoverage, setup.RoiMaxCoverage));
    }
    return enabled;
  }

  /// <summary>A camera's configured delay, never shorter than the floor.</summary>
  private static TimeSpan DelayFor(double hours)
  {
    if (double.IsNaN(hours) || hours <= 0)
      return MinDelay;
    var asked = TimeSpan.FromHours(hours);
    return asked < MinDelay ? MinDelay : asked;
  }

  private async Task<SweepResult> ProcessAsync(
    RoiTranscode.RoiTranscodeClient client, ClipStorage.RoiCandidate clip,
    RoiCameraSettings camera, CancellationToken ct)
  {
    var boxes = await BoxesForAsync(clip, ct);
    var duration = (clip.EndTs - clip.StartTs).TotalSeconds;
    var coverage = (float)RoiCoverage.Fraction(
      boxes.Select(b => b.T).ToList(), duration, _holdSec);

    // Outside the working range the clip is left alone, but the measurement is
    // still written: the thresholds are guesses until there is a distribution
    // from real cameras, and this is how that gets collected.
    if (coverage < camera.Floor || coverage > camera.Ceiling)
    {
      await clips.SetRoiStateAsync(
        clip.ClipUid, "skipped", null, false, coverage, CancellationToken.None);
      return SweepResult.Skipped;
    }

    var root = await RootPathAsync(clip.StorageId, ct);
    if (root is null)
    {
      logger.LogWarning("ROI transcode: storage {StorageId} has no root path", clip.StorageId);
      return SweepResult.Idle;
    }

    var input = Path.Combine(root, clip.FilePath);
    if (!File.Exists(input))
    {
      // The row outlived its file, which ClipCleaner will sort out. Coverage is
      // still recorded: it was measured, and it says something about the clip.
      await clips.SetRoiStateAsync(
        clip.ClipUid, "skipped", null, false, coverage, CancellationToken.None);
      return SweepResult.Skipped;
    }

    // Not beside the clip: ClipCleaner deletes by the glob "<basename>.*" in the
    // clip's own directory, so a temporary file there would be swept away with
    // the original -- or worse, survive as a half-written orphan.
    var tempDir = Path.Combine(root, ".roi_tmp");
    Directory.CreateDirectory(tempDir);
    var output = Path.Combine(tempDir, $"{clip.ClipUid}.mp4");

    var request = BuildRequest(input, output, boxes);
    var ok = false;
    // Whether the service was reachable at all. Not being able to talk to it says
    // nothing about the clip, and must not spend one of the clip's attempts --
    // otherwise a service that is briefly down, restarted or paused abandons
    // every clip the pass happened to reach while it was away, permanently and
    // with nothing wrong with any of them.
    var reachable = true;
    try
    {
      var started = await client.StartFileJobAsync(request, cancellationToken: ct);
      ok = await AwaitJobAsync(client, started.JobId, ct);
    }
    catch (RpcException ex) when (
      ex.StatusCode == StatusCode.Unavailable || ex.StatusCode == StatusCode.DeadlineExceeded)
    {
      reachable = false;
      logger.LogInformation("ROI transcode: service unreachable ({Status}), clip {ClipUid} left alone",
        ex.StatusCode, clip.ClipUid);
    }
    catch (Exception ex)
    {
      logger.LogInformation(ex, "ROI transcode: job refused for clip {ClipUid}", clip.ClipUid);
    }

    if (!ok)
    {
      TryDelete(output);
      await FailAsync(clip, reachable, ct);
      return reachable ? SweepResult.Worked : SweepResult.Unreachable;
    }

    await ReplaceAsync(clip, input, output, coverage, ct);
    return SweepResult.Worked;
  }

  private FileJobRequest BuildRequest(string input, string output, IReadOnlyList<Box> boxes)
  {
    var request = new FileJobRequest
    {
      InputPath = input,
      OutputPath = output,
      // Archive clips are fragmented mp4 played over byte ranges; a plain mp4
      // here would still decode standalone but stop working in the timeline.
      Movflags = "frag_keyframe+empty_moov+default_base_moof",
      Settings = new Settings
      {
        Encoder = Encoder.Auto,
        // The archive is H.264 and one playlist stitches many clips: re-encoding
        // some of them to HEVC would leave a single playlist carrying two codecs.
        Codec = Codec.H264,
        Crf = _crf,
        Roi = new RoiQuality
        {
          BackgroundQpDelta = _backgroundQpDelta,
          // The same window Coverage() measured with; see _holdSec.
          HoldSec = _holdSec,
          MaxRegions = 32,
        },
      },
    };
    // The detector's kinds are open-ended, and a kind the request does not name
    // would fall back to the module's own defaults rather than to the ones
    // configured here. So every kind actually present is registered -- with the
    // same quality today, and with somewhere obvious to put per-kind settings
    // the day they are wanted.
    foreach (var kind in boxes.Select(b => b.Kind).Distinct())
    {
      request.Settings.Roi.Kinds.Add(new KindQuality
      {
        Kind = kind,
        QpDelta = _qpDelta,
        Pad = _pad,
      });
    }
    request.Boxes.AddRange(boxes);
    return request;
  }

  /// <summary>
  /// The clip's boxes, on the module's timeline: seconds from the start of the
  /// file rather than wall clock.
  /// </summary>
  private async Task<List<Box>> BoxesForAsync(ClipStorage.RoiCandidate clip, CancellationToken ct)
  {
    var sources = await metadata.GetAsync(
      clip.CameraId, clip.StreamName, clip.StartTs, clip.EndTs, ct);
    if (sources.Count == 0)
      return [];

    // ONVIF timestamps come off the same clock that bounds clips; CV ones are
    // stamped after inference on another host and lag the frame they describe.
    var chosen = sources.FirstOrDefault(s => s.Source == "onvif") ?? sources[0];

    var boxes = new List<Box>();
    foreach (var frame in chosen.Frames)
    {
      var t = (frame.Ts.ToDateTime() - clip.StartTs).TotalSeconds;
      if (t < 0)
        continue;
      foreach (var b in frame.Boxes)
      {
        boxes.Add(new Box
        {
          T = t,
          Kind = b.Kind,
          X = b.X,
          Y = b.Y,
          Width = b.Width,
          Height = b.Height,
        });
      }
    }
    return boxes;
  }

  private async Task<bool> AwaitJobAsync(
    RoiTranscode.RoiTranscodeClient client, string jobId, CancellationToken ct)
  {
    var deadline = DateTime.UtcNow + JobTimeout;
    while (DateTime.UtcNow < deadline)
    {
      await Task.Delay(JobPoll, ct);
      var job = await client.GetJobAsync(new JobId { JobId_ = jobId }, cancellationToken: ct);
      if (job.State == JobState.Running)
        continue;
      if (job.State != JobState.Completed)
        logger.LogInformation("ROI transcode: job {JobId} ended as {State}: {Message}",
          jobId, job.State, job.Message);
      return job.State == JobState.Completed;
    }
    logger.LogWarning("ROI transcode: job {JobId} did not finish within {Timeout}", jobId, JobTimeout);
    return false;
  }

  /// <summary>Puts the smaller file in the clip's place, or throws none of it away if it cannot.</summary>
  private async Task ReplaceAsync(
    ClipStorage.RoiCandidate clip, string input, string output, float coverage,
    CancellationToken ct)
  {
    long size;
    try
    {
      size = new FileInfo(output).Length;
      if (size <= 0)
        throw new IOException("re-encoded file is empty");

      // The clip may have been deleted while the job ran. Checked before the
      // move rather than after, so the file is not restored under a row that no
      // longer exists.
      if (!await clips.ExistsAsync(clip.ClipUid, ct))
      {
        TryDelete(output);
        return;
      }

      File.Move(output, input, overwrite: true);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ROI transcode: could not replace clip {ClipUid}", clip.ClipUid);
      TryDelete(output);
      // Counted: the encode succeeded and the file is real, so a failure here is
      // about this clip -- a permission, a full disk, a vanished directory.
      await FailAsync(clip, countAttempt: true, ct);
      return;
    }

    if (!await clips.CompleteRoiTranscodeAsync(clip.ClipUid, size, coverage, CancellationToken.None))
    {
      // The row went away between the check and here. The file is already
      // replaced, which is harmless: ClipCleaner deletes by path.
      logger.LogInformation("ROI transcode: clip {ClipUid} vanished mid-replace", clip.ClipUid);
      return;
    }

    logger.LogInformation(
      "ROI transcode: clip {ClipUid} re-encoded, {Size} bytes, coverage {Coverage:P1}",
      clip.ClipUid, size, coverage);
  }

  /// <summary>
  /// Records a failed attempt. `countAttempt` is false when the service could not
  /// be reached: the clip stays pending with a plain pause rather than an
  /// exponential one, because there is nothing about it to back off from.
  /// </summary>
  private async Task FailAsync(ClipStorage.RoiCandidate clip, bool countAttempt, CancellationToken ct)
  {
    if (!countAttempt)
    {
      await clips.SetRoiStateAsync(
        clip.ClipUid, "pending", DateTime.UtcNow.AddSeconds(_retrySeconds), false, null,
        CancellationToken.None);
      return;
    }

    var attempts = clip.Attempts + 1;
    if (attempts >= _maxAttempts)
    {
      await clips.SetRoiStateAsync(clip.ClipUid, "failed", null, true, null, CancellationToken.None);
      return;
    }
    var capped = Math.Min(attempts, 16);
    var backoff = Math.Min((long)_retrySeconds << capped, MaxRetrySeconds);
    await clips.SetRoiStateAsync(
      clip.ClipUid, "pending", DateTime.UtcNow.AddSeconds(backoff), true, null,
      CancellationToken.None);
  }

  private async Task<string?> RootPathAsync(string storageId, CancellationToken ct)
  {
    var list = await storages.GetAsync(ct);
    return storages.Find(list, storageId)?.RootPath;
  }

  private static void TryDelete(string path)
  {
    try
    {
      if (File.Exists(path))
        File.Delete(path);
    }
    catch
    {
      // Best effort: a leftover in .roi_tmp costs space, not correctness.
    }
  }

  public async Task StopAsync(CancellationToken ct)
  {
    await _cts.CancelAsync();
    if (_loop is not null)
    {
      try { await _loop; } catch (OperationCanceledException) { }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync(CancellationToken.None);
    _cts.Dispose();
  }
}
