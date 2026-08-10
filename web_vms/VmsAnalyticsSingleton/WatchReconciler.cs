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
// keep-alive loop for active watches because media_server's MediaMTX push is lazy/idle-timeout-
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

  private sealed record ActiveWatch(string WorkerAppId, AnalyticsWatch Watch);

  // camera_id -> where it currently runs and with what config.
  private readonly ConcurrentDictionary<string, ActiveWatch> _active = new();
  // Reconciles are triggered concurrently (pub/sub handler, fallback timer, startup retry,
  // per-worker post-reconnect) — serialize the passes so two of them can't both decide a watch
  // isn't running and double-issue StartWatch (the worker's upsert would tear the pipeline down
  // mid-stream), or interleave a stop/start during a migration.
  private readonly SemaphoreSlim _reconcileLock = new(1, 1);
  // Cameras already warned about "enabled but not armed" — warn once per transition, not on
  // every reconcile pass (reconciles also fire on a periodic fallback timer).
  private readonly ConcurrentDictionary<string, bool> _warnedNotArmed = new();
  private readonly CancellationTokenSource _cts = new();

  private readonly string _mediaMtxReadUser =
    Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_USER")
    ?? throw new InvalidOperationException("Required environment variable 'VMS_MEDIAMTX_READ_USER' is not set");
  private readonly string _mediaMtxReadPass =
    Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_PASS")
    ?? throw new InvalidOperationException("Required environment variable 'VMS_MEDIAMTX_READ_PASS' is not set");

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
      var shouldRun = watch.Enabled && watch.Armed;
      var targetWorker = workers.SelectWorker(cameraId);
      _active.TryGetValue(cameraId, out var current);

      if (watch.Enabled && !watch.Armed)
      {
        if (_warnedNotArmed.TryAdd(cameraId, true))
          logger.LogWarning(
            "Analytics is enabled for camera '{CameraId}' but the camera is not armed — no detection will run until it is armed",
            cameraId);
      }
      else
      {
        _warnedNotArmed.TryRemove(cameraId, out _);
      }

      if (shouldRun && current is null)
      {
        if (await StartWatchOnWorkerAsync(targetWorker, watch, ct))
        {
          _active[cameraId] = new ActiveWatch(targetWorker, watch);
          logger.LogInformation("Analytics watch for camera '{CameraId}' started on worker '{Worker}'", cameraId, targetWorker);
        }
      }
      else if (shouldRun && current is not null)
      {
        if (current.WorkerAppId != targetWorker)
        {
          // Worker list changed and this camera's rendezvous winner moved.
          await StopWatchOnWorkerAsync(current.WorkerAppId, cameraId, ct);
          if (await StartWatchOnWorkerAsync(targetWorker, watch, ct))
          {
            _active[cameraId] = new ActiveWatch(targetWorker, watch);
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
          if (await StartWatchOnWorkerAsync(targetWorker, watch, ct))
            logger.LogInformation("Analytics watch for camera '{CameraId}' restarted with updated config", cameraId);
          _active[cameraId] = new ActiveWatch(targetWorker, watch);
        }
      }
      else if (!shouldRun && current is not null)
      {
        await StopWatchOnWorkerAsync(current.WorkerAppId, cameraId, ct);
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
        _active.TryRemove(cameraId, out _);
        logger.LogInformation("Analytics watch for camera '{CameraId}' no longer configured, stopped", cameraId);
      }
    }
  }

  private async Task<bool> StartWatchOnWorkerAsync(string workerAppId, AnalyticsWatch watch, CancellationToken ct)
  {
    var rtspUrl = await ResolveRtspUrlAsync(watch.CameraId, watch.StreamName, ct);
    if (rtspUrl is null) return false;

    // The relayed stream lives on MediaMTX, which no longer allows anonymous reads, so the worker
    // gets the vms_read service account. Passed as credentials rather than spliced into the URL so
    // the RTSP address stays safe to log on both sides.
    return await workers.ClientFor(workerAppId).StartWatchAsync(
      watch.CameraId, rtspUrl, _mediaMtxReadUser, _mediaMtxReadPass,
      watch.Classes, watch.MinConfidence, watch.SampleFps, watch.AttachDebugCrops, ct);
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

  // Builds the MediaMTX-relayed RTSP URL for camera_id/stream_name (never the camera's own raw
  // RTSP address — media_server already holds that connection) and best-effort nudges media_server
  // to make sure the MediaMTX push is actually live (it's lazy/idle-timeout-based, see project plan).
  private async Task<string?> ResolveRtspUrlAsync(string cameraId, string streamName, CancellationToken ct)
  {
    try
    {
      var camResp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!camResp.Found)
      {
        logger.LogWarning("Analytics: camera '{CameraId}' not found, cannot resolve RTSP source", cameraId);
        return null;
      }

      var serverResp = await cfg.GetOrAssignMediaMtxServerAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!serverResp.Found)
      {
        logger.LogWarning("Analytics: no MediaMTX server assigned for camera '{CameraId}'", cameraId);
        return null;
      }

      await EnsurePushAsync(cameraId, streamName, camResp.Setup.ServerId, ct);

      return $"rtsp://{serverResp.Server.Host}:{serverResp.Server.RtspPort}/{cameraId}/{streamName}";
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "Analytics: failed to resolve RTSP source for camera '{CameraId}'", cameraId);
      return null;
    }
  }

  private async Task EnsurePushAsync(string cameraId, string streamName, string serverId, CancellationToken ct)
  {
    try
    {
      var mediaClient = serverPairRegistry.GetMediaClient(serverId);
      await mediaClient.EnsureWebRtcStreamingAsync(
        new EnsureWebRtcStreamingRequest { CameraId = cameraId, StreamName = streamName },
        cancellationToken: ct);
    }
    catch (Exception ex)
    {
      // Best-effort: don't block StartWatch on this — the RTSP URL is still valid even if the
      // push isn't confirmed live yet; the worker's rtsp_reader will simply retry the connection.
      logger.LogWarning(ex, "Analytics: EnsureWebRtcStreaming failed for camera '{CameraId}'", cameraId);
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
          var camResp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
          if (!camResp.Found) continue;
          await EnsurePushAsync(cameraId, active.Watch.StreamName, camResp.Setup.ServerId, ct);
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
