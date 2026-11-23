using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  // The mandatory safety net behind the event: a lost message, a replica that died between reading
  // and writing, a camera whose streams were edited — all converge here. The full pass walks every
  // camera once every five minutes and writes only what differs; the thirty-second pass re-checks
  // the cameras whose video is known to be gone and the ones that have no video at all.
  internal sealed class CameraPresenceSweep : BackgroundService
  {
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FullInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private const int BatchSize = 1000;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly IMediaServerRegistry _servers;
    private readonly ILogger<CameraPresenceSweep> _logger;

    // One pass at a time: the full pass and the short one both write the same nodes.
    private readonly SemaphoreSlim _gate = new(1, 1);
    // What the last full pass saw, so the short pass does not list the whole configuration again.
    private readonly ConcurrentDictionary<string, CameraInfo> _known = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _noStreamCameras = [];
    private DateTime _nextFullAtUtc = DateTime.MinValue;

    public CameraPresenceSweep(
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      IMediaServerRegistry servers,
      ILogger<CameraPresenceSweep> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _servers = servers ?? throw new ArgumentNullException(nameof(servers));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      try
      {
        // Let the map's own startup resync build the nodes first; a state written before its node
        // exists is silently dropped.
        await Task.Delay(StartupDelay, stoppingToken);
        await RunPassAsync(full: true, stoppingToken);

        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
          await RunPassAsync(DateTime.UtcNow >= _nextFullAtUtc, stoppingToken);
      }
      catch (OperationCanceledException)
      {
        // Shutting down.
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Camera presence sweep stopped");
      }
    }

    internal async Task RunPassAsync(bool full, CancellationToken ct)
    {
      await _gate.WaitAsync(ct);
      try
      {
        if (full)
        {
          await FullPassAsync(ct);
          _nextFullAtUtc = DateTime.UtcNow.Add(FullInterval);
        }
        else
        {
          await NoVideoPassAsync(ct);
        }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Camera presence {Pass} pass failed", full ? "full" : "no-video");
      }
      finally
      {
        _gate.Release();
      }
    }

    private async Task FullPassAsync(CancellationToken ct)
    {
      // A decision is only valid for observations made after this instant: a state row written
      // later came from a fresher read (an event, or another replica) and must not be overwritten.
      var observedAt = DateTime.UtcNow;

      IReadOnlyList<string> serverIds;
      try
      {
        serverIds = await _servers.ServerIdsAsync(ct);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Camera presence: could not list the nodes");
        return;
      }

      var known = new Dictionary<string, CameraInfo>(StringComparer.Ordinal);
      var noStream = new List<string>();

      foreach (var serverId in serverIds)
      {
        List<CameraInfo> cameras;
        try
        {
          cameras = await _catalog.ListAsync(serverId, ct);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex,
            "Camera presence: could not list the cameras of server '{ServerId}'", serverId);
          continue;
        }

        foreach (var camera in cameras)
        {
          known[camera.CameraId] = camera;
          if (!camera.Disabled && !camera.HasConfiguredStreams)
            noStream.Add(camera.CameraId);
        }

        for (var offset = 0; offset < cameras.Count; offset += BatchSize)
        {
          await EvaluateBatchAsync(
            cameras.GetRange(offset, Math.Min(BatchSize, cameras.Count - offset)), observedAt, ct);
        }
      }

      _known.Clear();
      foreach (var (cameraId, camera) in known)
        _known[cameraId] = camera;
      _noStreamCameras = noStream;
    }

    private async Task NoVideoPassAsync(CancellationToken ct)
    {
      var observedAt = DateTime.UtcNow;

      var cameraIds = new HashSet<string>(StringComparer.Ordinal);
      await using (var scope = _scopes.CreateAsyncScope())
      {
        var store = scope.ServiceProvider.GetRequiredService<CameraPresenceStore>();
        cameraIds.UnionWith(await store.ListNoVideoCameraIdsAsync(ct));
      }
      cameraIds.UnionWith(_noStreamCameras);

      if (cameraIds.Count == 0)
        return;

      var cameras = new List<CameraInfo>(cameraIds.Count);
      foreach (var cameraId in cameraIds)
      {
        if (_known.TryGetValue(cameraId, out var cached))
        {
          cameras.Add(cached);
          continue;
        }

        try
        {
          var camera = await _catalog.GetAsync(cameraId, ct);
          if (camera is not null)
            cameras.Add(camera);
        }
        catch (Exception ex)
        {
          // A configuration read that failed is not a deleted camera; leave it for the next pass.
          _logger.LogDebug(ex, "Camera presence: could not read camera '{CameraId}'", cameraId);
        }
      }

      for (var offset = 0; offset < cameras.Count; offset += BatchSize)
      {
        await EvaluateBatchAsync(
          cameras.GetRange(offset, Math.Min(BatchSize, cameras.Count - offset)), observedAt, ct);
      }
    }

    private async Task EvaluateBatchAsync(
      IReadOnlyList<CameraInfo> cameras, DateTime observedAt, CancellationToken ct)
    {
      await using var scope = _scopes.CreateAsyncScope();
      var presence = scope.ServiceProvider.GetRequiredService<CameraPresenceService>();
      var store = scope.ServiceProvider.GetRequiredService<CameraPresenceStore>();
      var writer = scope.ServiceProvider.GetRequiredService<CameraStateWriter>();

      var candidates = await presence.DecideAsync(cameras, ct);
      if (candidates.Count == 0)
        return;

      var current = await store.LoadAsync(
        candidates.Select(candidate => candidate.Camera.CameraId).ToList(), ct);

      foreach (var candidate in candidates)
      {
        var cameraId = candidate.Camera.CameraId;
        var hasRow = current.TryGetValue(cameraId, out var row);
        if (hasRow && row!.Timestamp is { } writtenAt && writtenAt > observedAt)
          continue; // a fresher decision landed while this pass was reading

        var currentPresence = hasRow ? row!.Presence : (IReadOnlySet<string>)new HashSet<string>();
        var desired = new HashSet<string>(currentPresence, StringComparer.Ordinal);
        ApplyDecision(desired, candidate.Decision);

        if (desired.SetEquals(currentPresence))
          continue;

        await writer.ApplyPresenceAsync(
          cameraId, candidate.Decision.Connected, candidate.Decision.NoVideo, ct);
      }
    }

    private static void ApplyDecision(HashSet<string> states, PresenceDecision decision)
    {
      if (decision.Connected == true)
      {
        states.Remove(CameraStateWriter.Disconnected);
        states.Add(CameraStateWriter.Connected);
      }
      else if (decision.Connected == false)
      {
        states.Remove(CameraStateWriter.Connected);
        states.Add(CameraStateWriter.Disconnected);
      }

      if (decision.NoVideo == true)
        states.Add(CameraStateWriter.NoVideo);
      else if (decision.NoVideo == false)
        states.Remove(CameraStateWriter.NoVideo);
    }
  }
}
