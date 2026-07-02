using System.Collections.Concurrent;
using Grpc.Core;
using Vms.Cfg;
using Vms.Control;
using SourceRequest = Vms.Cfg.SourceRequest;

namespace VmsAnalytics;

// Diffs desired watch state (enabled + camera armed) against what's currently believed to be
// running on video_a, issues StartWatch/StopWatch, and resolves the MediaMTX RTSP URL for the
// camera on the fly — never stored, always derived (see project plan). Runs a keep-alive loop
// for active watches because media_server's MediaMTX push is lazy/idle-timeout-based and
// analytics itself doesn't otherwise look like a "viewer" to it.
// Not driven by ASP.NET Core's hosted-service startup ordering for reconciliation itself — that
// stays externally driven by AnalyticsWatchSubscriber on startup and on any relevant pub/sub
// signal — but IS registered as a hosted service so its keep-alive loop starts/stops with the app.
public sealed class WatchReconciler(
  IAnalyticsWorkerClient workerClient,
  CfgService.CfgServiceClient cfg,
  ServerPairRegistry serverPairRegistry,
  ILogger<WatchReconciler> logger) : IHostedService
{
  private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

  private readonly ConcurrentDictionary<string, AnalyticsWatch> _activeWatches = new();
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

  public async Task ReconcileAsync(IReadOnlyDictionary<string, AnalyticsWatch> watches, CancellationToken ct)
  {
    foreach (var (cameraId, watch) in watches)
    {
      var shouldRun = watch.Enabled && watch.Armed;
      var isRunning = _activeWatches.ContainsKey(cameraId);

      if (shouldRun && !isRunning)
      {
        var started = await StartWatchInternalAsync(watch, ct);
        if (started)
        {
          _activeWatches[cameraId] = watch;
          logger.LogInformation("Analytics watch for camera '{CameraId}' started", cameraId);
        }
      }
      else if (shouldRun && isRunning)
      {
        // Config may have changed (stream_name/classes/confidence/...) — refresh our cached copy
        // so the keep-alive loop always uses the current stream_name. Not re-issuing StartWatch
        // here on every reconcile keeps this cheap; a real config edit is rare enough that video_a
        // picking it up on the next natural StartWatch (camera re-armed, service restart) is fine.
        _activeWatches[cameraId] = watch;
      }
      else if (!shouldRun && isRunning)
      {
        await workerClient.StopWatchAsync(cameraId, ct);
        _activeWatches.TryRemove(cameraId, out _);
        logger.LogInformation("Analytics watch for camera '{CameraId}' stopped (disabled or camera not armed)", cameraId);
      }
    }

    // Stop watches that disappeared from VmsCfg entirely (removed via RemoveAnalyticsWatch).
    foreach (var cameraId in _activeWatches.Keys.ToList())
    {
      if (!watches.ContainsKey(cameraId))
      {
        await workerClient.StopWatchAsync(cameraId, ct);
        _activeWatches.TryRemove(cameraId, out _);
        logger.LogInformation("Analytics watch for camera '{CameraId}' no longer configured, stopped", cameraId);
      }
    }
  }

  private async Task<bool> StartWatchInternalAsync(AnalyticsWatch watch, CancellationToken ct)
  {
    var rtspUrl = await ResolveRtspUrlAsync(watch.CameraId, watch.StreamName, ct);
    if (rtspUrl is null) return false;

    return await workerClient.StartWatchAsync(
      watch.CameraId, rtspUrl, credUser: null, credPass: null,
      watch.Classes, watch.MinConfidence, watch.SampleFps, ct);
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
      // push isn't confirmed live yet; video_a's rtsp_reader will simply retry the connection.
      logger.LogWarning(ex, "Analytics: EnsureWebRtcStreaming failed for camera '{CameraId}'", cameraId);
    }
  }

  private async Task KeepAliveLoopAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      await Task.Delay(KeepAliveInterval, ct);
      foreach (var (cameraId, watch) in _activeWatches)
      {
        try
        {
          var camResp = await cfg.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
          if (!camResp.Found) continue;
          await EnsurePushAsync(cameraId, watch.StreamName, camResp.Setup.ServerId, ct);
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
