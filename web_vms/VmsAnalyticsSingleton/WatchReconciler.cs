using System.Collections.Concurrent;
using Grpc.Core;
using Vms.Cfg;
using Vms.Control;
using SourceRequest = Vms.Cfg.SourceRequest;

namespace VmsAnalyticsSingleton;

// Diffs desired watch state (enabled + camera armed) against what's currently believed to be
// running on each video_a worker, issues StartWatch/StopWatch, and resolves the MediaMTX RTSP
// URL for the camera on the fly — never stored, always derived (see project plan). Watches are
// sharded across workers by rendezvous hash (AnalyticsWorkerRegistry.SelectWorker). Runs a
// keep-alive loop for active watches because video_srv's MediaMTX push is lazy/idle-timeout-
// based and analytics itself doesn't otherwise look like a "viewer" to it.
// Reconciliation is externally driven by AnalyticsWatchSubscriber (startup + pub/sub signals);
// registered as a hosted service only so the keep-alive loop starts/stops with the app.
public sealed class WatchReconciler(
  AnalyticsWorkerRegistry workers,
  CfgService.CfgServiceClient cfg,
  ServerPairRegistry serverPairRegistry,
  ILogger<WatchReconciler> logger) : IHostedService
{
  private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

  /// <param name="ServerId">The media server publishing this camera to the worker.</param>
  /// <param name="ListenUrl">Where that worker asked to be published to.</param>
  private sealed record ActiveWatch(
    string WorkerAppId, AnalyticsWatch Watch, string ServerId = "", string ListenUrl = "");

  // camera_id -> where it currently runs and with what config.
  private readonly ConcurrentDictionary<string, ActiveWatch> _active = new();
  // Reconciles are triggered concurrently (pub/sub handler, fallback timer, startup retry,
  // per-worker post-reconnect) — serialize the passes so two of them can't both decide a watch
  // isn't running and double-issue StartWatch (the worker's upsert would tear the pipeline down
  // mid-stream), or interleave a stop/start during a migration.
  private readonly SemaphoreSlim _reconcileLock = new(1, 1);
  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = KeepAliveLoopAsync(_cts.Token);
    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  // Called when the StreamDetections channel to a worker drops: that worker may have restarted
  // and lost its (in-memory-only) watches, so forget what we think runs there — the next
  // reconcile will re-issue StartWatch for everything assigned to it.
  public void ResetAssumedState(string workerAppId)
  {
    foreach (var (cameraId, active) in _active)
    {
      if (active.WorkerAppId == workerAppId)
        _active.TryRemove(cameraId, out _);
    }
  }

  public async Task ReconcileAsync(IReadOnlyDictionary<string, AnalyticsWatch> watches, CancellationToken ct)
  {
    await _reconcileLock.WaitAsync(ct);
    try
    {
      await ReconcileLockedAsync(watches, ct);
    }
    finally
    {
      _reconcileLock.Release();
    }
  }

  private async Task ReconcileLockedAsync(IReadOnlyDictionary<string, AnalyticsWatch> watches, CancellationToken ct)
  {
    foreach (var (cameraId, watch) in watches)
    {
      // The watch's own switch decides whether the detector runs. Arming does not.
      //
      // It used to require both, which tied seeing to alarming: a camera recording around the
      // clock but not armed produced no geometry at all, so its archive had no overlay to draw
      // and the ROI pass had nothing to protect -- on exactly the footage that is kept longest.
      // The camera's own ONVIF boxes were never gated this way either, so the two sources
      // disagreed about when they existed.
      //
      // Arming still means what it should: the consumer only opens episodes, saves crops, writes
      // detection rows and activates alarm recording for an armed camera (DetectionStreamConsumer,
      // `persisted`). Geometry, the live overlay and the debug log were already unconditional
      // there, which is the split this restores rather than invents.
      var shouldRun = watch.Enabled;
      var targetWorker = workers.SelectWorker(cameraId);
      _active.TryGetValue(cameraId, out var current);

      if (shouldRun && current is null)
      {
        var started = await StartWatchOnWorkerAsync(targetWorker, watch, ct);
        if (started is not null)
        {
          _active[cameraId] = started;
          logger.LogInformation("Analytics watch for camera '{CameraId}' started on worker '{Worker}'", cameraId, targetWorker);
        }
      }
      else if (shouldRun && current is not null)
      {
        if (current.WorkerAppId != targetWorker)
        {
          // Worker list changed and this camera's rendezvous winner moved.
          await StopWatchOnWorkerAsync(current.WorkerAppId, cameraId, ct);
          await StopAnalyticsPushAsync(cameraId, current.Watch.StreamName, current.ServerId, ct);
          var migrated = await StartWatchOnWorkerAsync(targetWorker, watch, ct);
          if (migrated is not null)
          {
            _active[cameraId] = migrated;
            logger.LogInformation("Analytics watch for camera '{CameraId}' migrated '{From}' -> '{To}'", cameraId, current.WorkerAppId, targetWorker);
          }
          else
          {
            _active.TryRemove(cameraId, out _);
          }
        }
        else if (!watch.Equals(current.Watch))
        {
          // Config changed while running (stream_name/classes/confidence/debug crops/...) —
          // re-issue StartWatch so the worker picks it up; StartWatch is an idempotent upsert.
          var restarted = await StartWatchOnWorkerAsync(targetWorker, watch, ct);
          if (restarted is not null)
          {
            logger.LogInformation("Analytics watch for camera '{CameraId}' restarted with updated config", cameraId);
            _active[cameraId] = restarted;
          }
        }
      }
      else if (!shouldRun && current is not null)
      {
        await StopWatchOnWorkerAsync(current.WorkerAppId, cameraId, ct);
        await StopAnalyticsPushAsync(cameraId, current.Watch.StreamName, current.ServerId, ct);
        _active.TryRemove(cameraId, out _);
        logger.LogInformation("Analytics watch for camera '{CameraId}' stopped (disabled or camera not armed)", cameraId);
      }
    }

    // Stop watches that disappeared from VmsCfg entirely (removed via RemoveAnalyticsWatch).
    foreach (var (cameraId, active) in _active)
    {
      if (!watches.ContainsKey(cameraId))
      {
        await StopWatchOnWorkerAsync(active.WorkerAppId, cameraId, ct);
        await StopAnalyticsPushAsync(cameraId, active.Watch.StreamName, active.ServerId, ct);
        _active.TryRemove(cameraId, out _);
        logger.LogInformation("Analytics watch for camera '{CameraId}' no longer configured, stopped", cameraId);
      }
    }
  }

  /// <summary>
  /// Starts a watch and points the camera's media server at it.
  /// </summary>
  /// <remarks>
  /// The worker is published to rather than reading the stream back out of MediaMTX. Two readers
  /// of one high-bitrate path is what made the operator's live view stutter: MediaMTX pushes the
  /// same keyframe burst to both in turn, so the browser's share arrives spread over twice as
  /// long, misses its frame-assembly window, and asks for a keyframe a file source cannot produce
  /// on demand — the picture then holds until the next natural one. Measured with packetsLost 0
  /// and pliCount climbing regardless.
  ///
  /// So the worker opens an endpoint and answers with its address (only it knows what it is
  /// reachable at), and video_srv publishes there. Which worker holds which camera is decided
  /// here, as it always was; video_srv is told where to publish and knows nothing else about
  /// the arrangement.
  /// </remarks>
  private async Task<ActiveWatch?> StartWatchOnWorkerAsync(string workerAppId, AnalyticsWatch watch, CancellationToken ct)
  {
    var serverId = await ResolveMediaServerAsync(watch.CameraId, ct);
    if (serverId is null) return null;

    // No URL to dial and no credentials: the worker is the one being connected to.
    var listenUrl = await workers.ClientFor(workerAppId).StartWatchAsync(
      watch.CameraId, string.Empty, null, null,
      watch.Classes, watch.MinConfidence, watch.SampleFps, watch.AttachDebugCrops,
      listenForPush: true, ct);
    if (string.IsNullOrEmpty(listenUrl))
    {
      logger.LogWarning(
        "Analytics: worker '{Worker}' did not open a publish endpoint for camera '{CameraId}'",
        workerAppId, watch.CameraId);
      return null;
    }

    var pushed = await EnsureAnalyticsPushAsync(
      watch.CameraId, watch.StreamName, serverId, listenUrl, watch.SampleFps, ct);
    return pushed ? new ActiveWatch(workerAppId, watch, serverId, listenUrl) : null;
  }

  private async Task StopWatchOnWorkerAsync(string workerAppId, string cameraId, CancellationToken ct)
  {
    try
    {
      await workers.ClientFor(workerAppId).StopWatchAsync(cameraId, ct);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "StopWatch for camera '{CameraId}' on worker '{Worker}' failed", cameraId, workerAppId);
    }
  }

  /// <summary>Which media server holds this camera — the one that will publish it.</summary>
  private async Task<string?> ResolveMediaServerAsync(string cameraId, CancellationToken ct)
  {
    try
    {
      var camResp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (camResp.Found) return camResp.Setup.ServerId;
      logger.LogWarning("Analytics: camera '{CameraId}' not found, cannot start a watch", cameraId);
      return null;
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "Analytics: failed to resolve the media server for camera '{CameraId}'", cameraId);
      return null;
    }
  }

  /// <summary>
  /// Tells the camera's media server to publish the stream to the worker. Idempotent, so the
  /// keep-alive below can simply say it again: a media server that restarted has lost the sink
  /// and the worker would otherwise wait for a publish that never comes.
  /// </summary>
  private async Task<bool> EnsureAnalyticsPushAsync(
    string cameraId, string streamName, string serverId, string listenUrl, uint sampleFps,
    CancellationToken ct)
  {
    try
    {
      var mediaClient = serverPairRegistry.GetMediaClient(serverId);
      var status = await mediaClient.EnsureAnalyticsPushAsync(
        new EnsureAnalyticsPushRequest
        {
          CameraId = cameraId,
          StreamName = streamName,
          RtspUrl = listenUrl,
          SampleFps = sampleFps,
        },
        cancellationToken: ct);
      if (!status.Success)
        logger.LogWarning("Analytics: media server '{ServerId}' refused the push for camera "
          + "'{CameraId}': {Message}", serverId, cameraId, status.Message);
      return status.Success;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Analytics: EnsureAnalyticsPush failed for camera '{CameraId}'", cameraId);
      return false;
    }
  }

  private async Task StopAnalyticsPushAsync(
    string cameraId, string streamName, string serverId, CancellationToken ct)
  {
    if (string.IsNullOrEmpty(serverId)) return;
    try
    {
      var mediaClient = serverPairRegistry.GetMediaClient(serverId);
      await mediaClient.StopAnalyticsPushAsync(
        new AnalyticsPushKey { CameraId = cameraId, StreamName = streamName },
        cancellationToken: ct);
    }
    catch (Exception ex)
    {
      // A media server that is down has no sink to stop anyway.
      logger.LogWarning(ex, "Analytics: StopAnalyticsPush failed for camera '{CameraId}'", cameraId);
    }
  }

  private async Task KeepAliveLoopAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      await Task.Delay(KeepAliveInterval, ct);
      foreach (var (cameraId, active) in _active)
      {
        try
        {
          if (string.IsNullOrEmpty(active.ListenUrl) || string.IsNullOrEmpty(active.ServerId))
            continue;
          await EnsureAnalyticsPushAsync(
            cameraId, active.Watch.StreamName, active.ServerId, active.ListenUrl,
            active.Watch.SampleFps, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          break;
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Analytics: keep-alive failed for camera '{CameraId}'", cameraId);
        }
      }
    }
  }
}
