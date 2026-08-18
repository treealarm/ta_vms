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
  private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(5);
  // Generous: a long clip on the CPU fallback is minutes of work, and giving up
  // early would leave the service encoding into a file nobody waits for.
  private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);
  private const int BatchSize = 20;
  private const long MaxRetrySeconds = 6 * 60 * 60;

  private static int EnvInt(string name, int fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var v) && v > 0 ? v : fallback;
  }

  private static double EnvDouble(string name, double fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    return double.TryParse(raw, System.Globalization.NumberStyles.Float,
      System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
  }

  private static int EnvSigned(string name, int fallback)
  {
    var raw = Environment.GetEnvironmentVariable(name);
    return int.TryParse(raw, out var v) ? v : fallback;
  }

  private readonly int _settleSeconds = EnvInt("VMS_ROI_SETTLE_SECONDS", 300);
  private readonly int _retrySeconds = EnvInt("VMS_ROI_RETRY_SECONDS", 600);
  private readonly int _maxAttempts = EnvInt("VMS_ROI_MAX_ATTEMPTS", 3);
  private readonly int _crf = EnvInt("VMS_ROI_CRF", 28);
  private readonly int _qpDelta = EnvSigned("VMS_ROI_QP_DELTA", -8);
  private readonly int _backgroundQpDelta = EnvSigned("VMS_ROI_BACKGROUND_QP_DELTA", 8);
  private readonly double _pad = EnvDouble("VMS_ROI_PAD", 0.05);

  private readonly CancellationTokenSource _cts = new();
  private Task? _loop;

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
        await Task.Delay(Tick, ct);
        if (!_scopeReady)
          continue;
        await SweepAsync(client, ct);
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

  private async Task SweepAsync(RoiTranscode.RoiTranscodeClient client, CancellationToken ct)
  {
    var enabled = await EnabledCamerasAsync(ct);
    if (enabled.Count == 0)
      return;

    var candidates = await clips.GetRoiCandidatesAsync(enabled, _settleSeconds, BatchSize, ct);
    foreach (var clip in candidates)
    {
      if (ct.IsCancellationRequested)
        return;
      // One clip per tick. Re-encoding competes with recording for the same
      // machine, and on the CPU fallback it competes hard.
      if (await ProcessAsync(client, clip, ct))
        return;
    }
  }

  /// <summary>Cameras on this node whose owner asked for ROI re-compression.</summary>
  private async Task<List<string>> EnabledCamerasAsync(CancellationToken ct)
  {
    var ids = await clips.GetRoiCameraIdsAsync(ct);
    var enabled = new List<string>();
    foreach (var cameraId in ids)
    {
      var setup = await cameras.GetAsync(cameraId, ct);
      if (setup is null || !setup.RoiTranscode)
        continue;
      if (!string.IsNullOrEmpty(OwnServerId)
        && !string.Equals(setup.ServerId, OwnServerId, StringComparison.OrdinalIgnoreCase))
        continue;
      enabled.Add(cameraId);
    }
    return enabled;
  }

  private async Task<bool> ProcessAsync(
    RoiTranscode.RoiTranscodeClient client, ClipStorage.RoiCandidate clip, CancellationToken ct)
  {
    var boxes = await BoxesForAsync(clip, ct);
    if (boxes.Count == 0)
    {
      // Nothing to protect. Marked rather than retried: the boxes are written
      // shortly after the clip closes, so by the settle window they are either
      // there or they never will be.
      await clips.SetRoiStateAsync(clip.ClipUid, "skipped", null, false, CancellationToken.None);
      return false;
    }

    var root = await RootPathAsync(clip.StorageId, ct);
    if (root is null)
    {
      logger.LogWarning("ROI transcode: storage {StorageId} has no root path", clip.StorageId);
      return false;
    }

    var input = Path.Combine(root, clip.FilePath);
    if (!File.Exists(input))
    {
      await clips.SetRoiStateAsync(clip.ClipUid, "skipped", null, false, CancellationToken.None);
      return false;
    }

    // Not beside the clip: ClipCleaner deletes by the glob "<basename>.*" in the
    // clip's own directory, so a temporary file there would be swept away with
    // the original -- or worse, survive as a half-written orphan.
    var tempDir = Path.Combine(root, ".roi_tmp");
    Directory.CreateDirectory(tempDir);
    var output = Path.Combine(tempDir, $"{clip.ClipUid}.mp4");

    var request = BuildRequest(input, output, boxes);
    var ok = false;
    try
    {
      var started = await client.StartFileJobAsync(request, cancellationToken: ct);
      ok = await AwaitJobAsync(client, started.JobId, ct);
    }
    catch (Exception ex)
    {
      logger.LogInformation(ex, "ROI transcode: job refused for clip {ClipUid}", clip.ClipUid);
    }

    if (!ok)
    {
      TryDelete(output);
      await FailAsync(clip, ct);
      return true;
    }

    await ReplaceAsync(clip, input, output, ct);
    return true;
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
    ClipStorage.RoiCandidate clip, string input, string output, CancellationToken ct)
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
      await FailAsync(clip, ct);
      return;
    }

    if (!await clips.CompleteRoiTranscodeAsync(clip.ClipUid, size, CancellationToken.None))
    {
      // The row went away between the check and here. The file is already
      // replaced, which is harmless: ClipCleaner deletes by path.
      logger.LogInformation("ROI transcode: clip {ClipUid} vanished mid-replace", clip.ClipUid);
      return;
    }

    logger.LogInformation("ROI transcode: clip {ClipUid} re-encoded, {Size} bytes", clip.ClipUid, size);
  }

  private async Task FailAsync(ClipStorage.RoiCandidate clip, CancellationToken ct)
  {
    var attempts = clip.Attempts + 1;
    if (attempts >= _maxAttempts)
    {
      await clips.SetRoiStateAsync(clip.ClipUid, "failed", null, true, CancellationToken.None);
      return;
    }
    var capped = Math.Min(attempts, 16);
    var backoff = Math.Min((long)_retrySeconds << capped, MaxRetrySeconds);
    await clips.SetRoiStateAsync(
      clip.ClipUid, "pending", DateTime.UtcNow.AddSeconds(backoff), true, CancellationToken.None);
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
