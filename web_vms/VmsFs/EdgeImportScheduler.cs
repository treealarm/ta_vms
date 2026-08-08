using Dapr.Client;
using Vms.Cfg;
using Vms.Control;
using Vms.Onvif;

namespace VmsFs;

/// <summary>
/// Recovers archive gaps from the cameras' own storage without being asked.
/// </summary>
/// <remarks>
/// Lives here because this is where the gaps are: VmsFs owns <c>source_outages</c> and is paired
/// one-to-one with the media server that would do the pulling, so the work is already scoped to a
/// node. It never competes with the operator — a gap being worked on manually is <c>filling</c> or
/// <c>held</c>, and neither is picked up here.
///
/// Asking VmsOnvif from here is the one dependency that does not follow the usual direction: VmsFs
/// is sharded per node while VmsOnvif is a single instance for the whole system. It is accepted for
/// now — the calls are rare and VmsOnvif is already the shared gateway to every camera — and is to
/// be revisited when sharding is looked at properly. The alternative is to move this scheduler to
/// VmsUtils, which orchestrates across services already and can reach each node's VmsFs through the
/// server↔fs pairing; every RPC it would need exists.
///
/// Two properties of the device shape the pacing. Its archive is a ring buffer roughly an hour long,
/// so the backlog is worked oldest-first: that is the part about to disappear. And a stretch that
/// has just been recorded is not offered for replay immediately, so a gap is left to settle before
/// the first attempt and retries are spaced out — asking again straight away returns the same
/// nothing and only burns the attempt budget.
/// </remarks>
public sealed class EdgeImportScheduler(
  SourceOutageStore outages,
  EdgeImportJobStore jobs,
  CfgService.CfgServiceClient cfg,
  OnvifService.OnvifServiceClient onvif,
  ClipStorage clips,
  ILogger<EdgeImportScheduler> logger) : IHostedService, IAsyncDisposable
{
  private static TimeSpan EnvSeconds(string name, int fallback) =>
    TimeSpan.FromSeconds(
      int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback);

  /// <summary>How long to leave a gap alone after it closed, before asking the camera for it.</summary>
  private static readonly TimeSpan Settle = EnvSeconds("VMS_BACKFILL_SETTLE_SECONDS", 60);

  /// <summary>Minimum spacing between attempts on the same gap.</summary>
  private static readonly TimeSpan RetryInterval = EnvSeconds("VMS_BACKFILL_RETRY_SECONDS", 300);

  /// <summary>Nothing older than this is attempted — the card cannot possibly still hold it.</summary>
  private static readonly TimeSpan Lookback = TimeSpan.FromHours(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_BACKFILL_LOOKBACK_HOURS"), out var h) && h > 0
      ? h : 48);

  private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
  /// <summary>
  /// How long a claimed job stays claimed. Renewed while it runs; once it lapses the job is free
  /// again, which is how a pull interrupted by a restart finds its way back into the queue.
  /// </summary>
  private static readonly TimeSpan JobLease = TimeSpan.FromMinutes(2);
  /// <summary>Finished jobs are operational history, not archive — they age out quickly.</summary>
  private static readonly TimeSpan JobRetention = TimeSpan.FromHours(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_BACKFILL_JOB_RETENTION_HOURS"), out var jr) && jr > 0
      ? jr : 24);
  private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
  private DateTime _lastCleanup = DateTime.MinValue;
  private static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);
  /// <summary>Asked for slightly wider than the hole, so no frames are lost at the seams.</summary>
  private static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Slivers shorter than this are not holes. Clip boundaries land on keyframes and the recorded
  /// interval on a report, so the two never agree to the millisecond; without a tolerance a gap
  /// covered but for a few milliseconds is retried until its budget runs out, and each retry costs
  /// one of the camera's few RTSP sessions to fetch nothing.
  /// </summary>
  private static readonly TimeSpan FillTolerance = TimeSpan.FromSeconds(1);

  /// <summary>
  /// How old a gap must be before an empty answer from the camera may be read as "gone for good".
  /// </summary>
  /// <remarks>
  /// The card is a ring buffer, so material only ever disappears — but that is true of old footage.
  /// A stretch recorded minutes ago can be missing from a search simply because the device has not
  /// finished indexing it, and calling that "exhausted" is irreversible: the gap would never be
  /// attempted again although the footage is right there.
  /// </remarks>
  private static readonly TimeSpan ExhaustAfter = EnvSeconds("VMS_BACKFILL_EXHAUST_AFTER_SECONDS", 900);

  private readonly CancellationTokenSource _cts = new();
  private Task? _task;

  /// <summary>Only this node's cameras; the two VmsFs instances share one database.</summary>
  public string? OwnServerId { get; private set; }
  private volatile bool _scopeReady;

  public void SetScope(string? serverId)
  {
    OwnServerId = serverId;
    _scopeReady = true;
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _task = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        await Task.Delay(Tick, ct);
        if (!_scopeReady)
          continue;
        try
        {
          await SweepAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
          logger.LogWarning(ex, "Edge backfill sweep failed");
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  private async Task SweepAsync(CancellationToken ct)
  {
    var enabledStreams = await EnabledStreamsAsync(ct);
    var enabledCameras = enabledStreams.Select(k => k.Camera).Distinct().ToList();

    // Anything already queued comes first — including work a previous run of this process left
    // behind, which is now simply a job whose lease has lapsed.
    if (enabledCameras.Count > 0 && await RunQueuedAsync(enabledCameras, ct))
      return;

    await CleanupAsync(ct);

    var now = DateTime.UtcNow;
    // A pull has a hard deadline of JobTimeout; a row still marked as running well past that
    // belongs to a process that is no longer here.
    var pending = await outages.ListPendingAsync(
      now - Settle, now - Lookback, now - JobTimeout, 20, ct);
    if (pending.Count == 0)
      return;

    if (enabledStreams.Count == 0)
      return;

    // One camera per sweep at most: the device has very few RTSP sessions, and a second job would be
    // refused anyway. Oldest first — that is the material closest to falling off the card.
    var busyCameras = new HashSet<string>();
    foreach (var outage in pending)
    {
      if (ct.IsCancellationRequested)
        return;
      var key = (outage.CameraId, outage.StreamName ?? string.Empty);
      if (!enabledStreams.Contains(key) || !busyCameras.Add(outage.CameraId))
        continue;

      // Spaced regardless of the outcome, so a failing camera is retried on a schedule rather than
      // as fast as the loop can go.
      await outages.SetNextRetryAsync(outage.OutageUid, DateTime.UtcNow + RetryInterval, ct);
      await AttemptAsync(outage, ct);
    }
  }

  /// <summary>
  /// The one stream per camera whose gaps may be recovered, for cameras on this node that asked.
  /// </summary>
  /// <remarks>
  /// One stream and not several: the camera's storage holds a single archive, recorded from either
  /// its main or its sub stream, and which one is chosen inside the camera — ONVIF does not report
  /// it (GetRecordingJobs comes back empty on the devices tested). Filling both of our streams from
  /// it would store the same footage twice under two different qualities.
  /// </remarks>
  private async Task<HashSet<(string Camera, string Stream)>> EnabledStreamsAsync(CancellationToken ct)
  {
    var request = new ListCamerasRequest();
    if (OwnServerId is not null)
      request.ServerId = OwnServerId;

    var response = await cfg.ListCamerasAsync(request, cancellationToken: ct);
    var result = new HashSet<(string, string)>();
    foreach (var camera in response.Cameras)
    {
      if (camera.HasDisabled && camera.Disabled)
        continue;
      if (string.IsNullOrWhiteSpace(camera.EdgeBackfillStream))
        continue;

      var stream = camera.Streams.FirstOrDefault(s => s.StreamName == camera.EdgeBackfillStream);
      if (stream is null || (stream.HasDisabled && stream.Disabled))
        continue;

      result.Add((camera.CameraId, stream.StreamName));
    }
    return result;
  }

  private async Task AttemptAsync(SourceOutage outage, CancellationToken ct)
  {
    if (outage.EndedTs is not { } endedTs)
      return;

    // What is still missing, taken from the archive rather than from the row: clips written by an
    // earlier attempt — or by the operator — are simply no longer missing, so a resumed gap
    // continues where it stopped instead of pulling everything again.
    var missing = await MissingAsync(outage, endedTs, ct);
    if (missing.Count == 0)
    {
      await FinishAsync(outage, "filled", string.Empty, ct);
      return;
    }

    var available = await OnCameraAsync(outage.CameraId, missing, ct);
    if (available is null)
    {
      // The camera could not be asked. That is not the same as having nothing, so the gap stays
      // open and is tried again later.
      return;
    }
    if (available.Count == 0)
    {
      // Two independent reasons not to believe a single empty answer. The device may still be
      // indexing what it has just recorded — and a camera in trouble answers a search without
      // finding anything at all, which is a statement about the network, not about the card.
      // Declaring it exhausted is irreversible, so it takes both age and a repeat.
      if (DateTime.UtcNow - endedTs < ExhaustAfter || outage.Attempts < 1)
      {
        logger.LogDebug("Edge backfill: {CameraId} has nothing for the gap yet, will retry",
          outage.CameraId);
        await outages.UpdateOutcomeAsync(outage.OutageUid, "open", outage.FilledMs,
          outage.Attempts + 1, "the camera returned nothing for this period", ct);
        return;
      }
      await FinishAsync(outage, "exhausted", "the camera no longer holds the missing period", ct);
      return;
    }

    logger.LogInformation(
      "Edge backfill: camera {CameraId}/{StreamName} gap {From:o}..{To:o}, {Count} piece(s) to pull",
      outage.CameraId, outage.StreamName, outage.StartedTs, endedTs, available.Count);

    // Queued, not pulled here. The unit of work becomes a row, so a restart in the middle of a
    // twenty-minute recovery resumes it instead of starting over — and the same queue is where the
    // manual button will put its work, so the two can never fight over the camera.
    foreach (var piece in available)
    {
      await jobs.EnqueueAsync(outage.CameraId, outage.StreamName ?? string.Empty,
        piece.Start, piece.End, outage.OutageUid, ct);
    }

    await outages.UpdateOutcomeAsync(
      outage.OutageUid, "filling", outage.FilledMs, outage.Attempts, string.Empty, ct);

    // The outcome is judged from the archive afterwards, never from "the job reported success":
    // a job can finish cleanly and have recovered nothing.
    var stillMissing = await MissingAsync(outage, endedTs, ct);
    var filledMs = (long)(endedTs - outage.StartedTs - stillMissing.Aggregate(
      TimeSpan.Zero, (acc, m) => acc + (m.End - m.Start))).TotalMilliseconds;
    var progressed = filledMs > outage.FilledMs;

    if (stillMissing.Count == 0)
    {
      await FinishAsync(outage, "filled", string.Empty, ct, filledMs);
      return;
    }

    // Counts only fruitless attempts; any progress resets it, so a long gap recovered piece by piece
    // never runs out of budget precisely because it is succeeding.
    // Left as filling while its jobs are outstanding; the run that drains them decides the rest.
  }

  /// <summary>
  /// Runs one queued import to completion. True when something was taken on, so the sweep stops
  /// there — the camera serves one replay at a time and there is no point planning more work while
  /// a pull is in flight.
  /// </summary>
  private async Task<bool> RunQueuedAsync(IReadOnlyCollection<string> cameras, CancellationToken ct)
  {
    var job = await jobs.ClaimAsync(cameras, JobLease, ct);
    if (job is null)
      return false;

    // The operator's decision outranks anything already queued: a gap they took charge of must not
    // keep being worked on just because the work was planned before they pressed the button.
    if (job.OutageUid is { } gapUid)
    {
      var rows = await outages.ListAsync(null, null, null, 1, ct, gapUid);
      var state = rows.FirstOrDefault()?.State;
      if (state is "held" or "ignored")
      {
        await jobs.FinishAsync(job.JobUid, "cancelled", $"gap is {state}", ct);
        return true;
      }
    }

    logger.LogInformation("Edge backfill: running job {JobUid} for {CameraId} {From:o}..{To:o}",
      job.JobUid, job.CameraId, job.RangeStart, job.RangeEnd);

    try
    {
      var ok = await PullAsync(job, ct);
      await jobs.FinishAsync(job.JobUid, ok ? "succeeded" : "failed",
        ok ? string.Empty : "the camera did not deliver this range", ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      logger.LogWarning(ex, "Edge backfill: job {JobUid} failed", job.JobUid);
      await jobs.FinishAsync(job.JobUid, "failed", ex.Message, CancellationToken.None);
    }

    // Handed back as open so the next sweep re-judges it from the archive. Merely clearing the
    // retry time is not enough: a row left saying "filling" is only reconsidered once its runner is
    // presumed dead, half an hour later — so a gap whose work had just finished sat there with the
    // recovered footage already in place and nothing to notice it.
    if (job.OutageUid is { } outageUid)
    {
      var rows = await outages.ListAsync(null, null, null, 1, CancellationToken.None, outageUid);
      if (rows.FirstOrDefault() is { } gap && gap.State == "filling")
      {
        await outages.UpdateOutcomeAsync(
          outageUid, "open", gap.FilledMs, gap.Attempts, string.Empty, CancellationToken.None);
      }
      await outages.SetNextRetryAsync(outageUid, DateTime.UtcNow, CancellationToken.None);
    }

    return true;
  }

  private async Task CleanupAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _lastCleanup < CleanupInterval)
      return;
    _lastCleanup = DateTime.UtcNow;

    var removed = await jobs.DeleteFinishedOlderThanAsync(DateTime.UtcNow - JobRetention, ct);
    if (removed > 0)
      logger.LogInformation("Removed {Count} finished edge import job(s)", removed);
  }

  private async Task FinishAsync(
    SourceOutage outage, string state, string message, CancellationToken ct, long? filledMs = null)
  {
    await outages.UpdateOutcomeAsync(
      outage.OutageUid, state, filledMs ?? outage.FilledMs, outage.Attempts, message, ct);
    logger.LogInformation("Edge backfill: gap {Uid} for {CameraId} is now {State}",
      outage.OutageUid, outage.CameraId, state);
  }

  /// <summary>Runs one import and waits for it, because the camera can only serve one at a time.</summary>
  private async Task<bool> PullAsync(EdgeImportJob job, CancellationToken ct)
  {
    // Which recording holds this stretch is asked for now rather than remembered from planning:
    // replay URIs are commonly single-use, and a queued job may be run much later than it was made.
    var onCamera = await OnCameraAsync(job.CameraId,
      [(job.RangeStart, job.RangeEnd)], ct);
    if (onCamera is null || onCamera.Count == 0)
      return false;
    var piece = onCamera[0];

    var camera = await cfg.GetCameraAsync(
      new Vms.Cfg.SourceRequest { SourceId = job.CameraId }, cancellationToken: ct);
    if (!camera.Found || camera.Setup is not { } setup || string.IsNullOrWhiteSpace(setup.ServerId))
      return false;

    // Resolved immediately before use: devices commonly hand out single-use replay URIs.
    var replay = await onvif.GetEdgeReplayUriAsync(new EdgeReplayUriRequest
    {
      SourceId = job.CameraId,
      RecordingToken = piece.RecordingToken,
    }, cancellationToken: ct);

    if (string.IsNullOrWhiteSpace(replay.Uri))
      return false;
    if (!replay.HasClockOffsetMs)
    {
      // The card is indexed in the camera's clock. Without knowing the difference the pull would
      // land on a different stretch of footage, which is worse than not pulling at all.
      logger.LogWarning("Edge backfill: camera {CameraId} does not report its clock; skipping",
        job.CameraId);
      return false;
    }

    var offset = TimeSpan.FromMilliseconds(replay.ClockOffsetMs);
    var start = piece.Start - Margin;
    var end = piece.End + Margin;

    var request = new EdgeImportRequest
    {
      CameraId = job.CameraId,
      StreamName = job.StreamName,
      ReplayUri = replay.Uri,
      StartUnixMs = ToUnixMs(start),
      EndUnixMs = ToUnixMs(end),
      SeekStartUnixMs = ToUnixMs(start + offset),
      SeekEndUnixMs = ToUnixMs(end + offset),
      ExportToFile = false,
    };
    if (setup.Cred is { } cred)
      request.Cred = new Vms.Control.Credentials
      {
        User = cred.User ?? string.Empty,
        Pass = cred.Pass ?? string.Empty,
      };

    var control = new CameraControlService.CameraControlServiceClient(
      DaprClient.CreateInvocationInvoker(setup.ServerId));

    try
    {
      var started = await control.StartEdgeImportAsync(request, cancellationToken: ct);
      // Recorded before waiting, so a runner that takes this job over after a restart can tell that
      // a media-server job was already started for it.
      await jobs.SetMediaJobAsync(job.JobUid, started.JobId, CancellationToken.None);
      return await AwaitJobAsync(control, job.JobUid, started.JobId, ct);
    }
    catch (Exception ex)
    {
      // A camera out of RTSP sessions answers 453 and the job is refused; that is worth retrying
      // later, not worth failing the gap over.
      logger.LogInformation(ex, "Edge backfill: import refused for {CameraId}", job.CameraId);
      return false;
    }
  }

  private async Task<bool> AwaitJobAsync(
    CameraControlService.CameraControlServiceClient control, Guid jobUid, string mediaJobId,
    CancellationToken ct)
  {
    var deadline = DateTime.UtcNow + JobTimeout;
    while (DateTime.UtcNow < deadline)
    {
      await Task.Delay(JobPoll, ct);
      // The lease is what tells everyone else this work still has an owner; a long pull would
      // otherwise look abandoned and be claimed a second time.
      await jobs.RenewAsync(jobUid, JobLease, CancellationToken.None);

      var state = await control.GetEdgeImportJobAsync(
        new EdgeImportJobKey { JobId = mediaJobId }, cancellationToken: ct);
      if (state.State == EdgeImportState.Running)
        continue;
      return state.State == EdgeImportState.Completed;
    }
    return false;
  }

  /// <summary>The parts of the gap the archive still does not have.</summary>
  private async Task<List<(DateTime Start, DateTime End)>> MissingAsync(
    SourceOutage outage, DateTime endedTs, CancellationToken ct)
  {
    var request = new Vms.Fs.GetRecordingTimelineRequest
    {
      CameraIds = { outage.CameraId },
      StreamName = outage.StreamName ?? string.Empty,
      FromTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
        DateTime.SpecifyKind(outage.StartedTs, DateTimeKind.Utc)),
      ToTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(
        DateTime.SpecifyKind(endedTs, DateTimeKind.Utc)),
      MinGapMs = 1000,
    };

    var tracks = await clips.GetRecordingTimelineAsync(request, ct);
    // Only footage brought back from the camera closes a gap. Live clips that happen to fall inside
    // the window are exactly the ones written while the source was failing — truncated, or ending
    // mid-write — so treating them as coverage leaves the hole half-filled with what broke it.
    var covered = tracks
      .SelectMany(t => t.Intervals)
      .Where(i => i.Origin == "edge")
      .Select(i => (Start: i.StartTs.ToDateTime(), End: i.EndTs.ToDateTime()))
      .OrderBy(i => i.Start)
      .ToList();

    var result = new List<(DateTime, DateTime)>();
    var cursor = outage.StartedTs;
    foreach (var (start, end) in covered)
    {
      if (start > cursor)
        result.Add((cursor, start < endedTs ? start : endedTs));
      if (end > cursor)
        cursor = end;
      if (cursor >= endedTs)
        break;
    }
    if (cursor < endedTs)
      result.Add((cursor, endedTs));
    return result.Where(r => r.Item2 - r.Item1 >= FillTolerance).ToList();
  }

  /// <summary>
  /// What of the missing time the camera still holds, with the recording each piece belongs to.
  /// Null when the camera could not be asked — which must never be read as "it has nothing".
  /// </summary>
  private async Task<List<(DateTime Start, DateTime End, string RecordingToken)>?> OnCameraAsync(
    string cameraId, List<(DateTime Start, DateTime End)> missing, CancellationToken ct)
  {
    try
    {
      var response = await onvif.ListEdgeRecordingIntervalsAsync(new EdgeIntervalsRequest
      {
        SourceId = cameraId,
        StartTime = missing[0].Start.ToString("O"),
        EndTime = missing[^1].End.ToString("O"),
        MaxResults = 200,
      }, cancellationToken: ct);

      if (!response.Searched)
        return null;

      var available = MergeSpans(response.Intervals
        .Select(i => (
          Start: DateTime.Parse(i.StartTime).ToUniversalTime(),
          End: DateTime.Parse(i.EndTime).ToUniversalTime(),
          RecordingToken: i.RecordingToken)));

      return missing
        .SelectMany(m => available
          .Select(a => (
            Start: m.Start > a.Start ? m.Start : a.Start,
            End: m.End < a.End ? m.End : a.End,
            RecordingToken: a.RecordingToken))
          .Where(x => x.End > x.Start))
        .OrderBy(x => x.Start)
        .ToList();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Edge backfill: could not ask camera {CameraId} what it holds", cameraId);
      return null;
    }
  }

  /// <summary>
  /// Collapses the device's intervals into non-overlapping spans.
  /// </summary>
  /// <remarks>
  /// A recording search answers per track — video, audio and metadata each report the same stretch
  /// of time — so without this the same footage is planned once per track and pulled that many
  /// times. The recording token of the first span in a run is kept; they all belong to the same
  /// recording, which is what the replay URI is resolved from.
  /// </remarks>
  private static List<(DateTime Start, DateTime End, string RecordingToken)> MergeSpans(
    IEnumerable<(DateTime Start, DateTime End, string RecordingToken)> spans)
  {
    var result = new List<(DateTime Start, DateTime End, string RecordingToken)>();
    foreach (var span in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
    {
      if (result.Count > 0 && span.Start <= result[^1].End)
      {
        if (span.End > result[^1].End)
          result[^1] = (result[^1].Start, span.End, result[^1].RecordingToken);
        continue;
      }
      result.Add(span);
    }
    return result;
  }

  private static long ToUnixMs(DateTime value) =>
    new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    await _cts.CancelAsync();
    if (_task != null)
    {
      try { await _task; } catch (OperationCanceledException) { }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync(CancellationToken.None);
    _cts.Dispose();
  }
}
