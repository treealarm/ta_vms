using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  internal sealed record PresenceCandidate(CameraInfo Camera, PresenceDecision Decision);

  // Sits between the two channels and the camera's node: reads the video state, asks the one's own
  // node whether the camera answers at all where that is worth asking, decides, writes the whole
  // presence set in one operation. Events bring a camera here; the sweep brings a batch.
  internal sealed class CameraPresenceService(
    ICameraVideoStateSource video,
    ICameraReachabilityProbe reachability,
    CameraStateWriter writer,
    ILogger<CameraPresenceService> logger)
  {
    private const int ProbeParallelism = 8;

    /// <summary>Re-evaluates one camera and writes what changed.</summary>
    public async Task EvaluateAsync(CameraInfo camera, CancellationToken ct)
    {
      foreach (var candidate in await DecideAsync([camera], ct))
      {
        await writer.ApplyPresenceAsync(
          candidate.Camera.CameraId, candidate.Decision.Connected, candidate.Decision.NoVideo, ct);
      }
    }

    /// <summary>
    /// The decisions for a batch, without writing. A camera with nothing to conclude is left out,
    /// so a sweep does not open a transaction for it.
    /// </summary>
    public async Task<IReadOnlyList<PresenceCandidate>> DecideAsync(
      IReadOnlyList<CameraInfo> cameras, CancellationToken ct)
    {
      if (cameras.Count == 0)
        return [];

      var states = await video.GetAsync(cameras, ct);
      var summaries = new Dictionary<string, CameraVideoSummary>(StringComparer.Ordinal);
      foreach (var camera in cameras)
      {
        summaries[camera.CameraId] =
          CameraPresenceRules.Summarize(camera, states.GetValueOrDefault(camera.CameraId));
      }

      var toProbe = cameras
        .Where(camera => !camera.Disabled
          && CameraPresenceRules.ShouldProbe(camera, summaries[camera.CameraId]))
        .ToList();
      var probes = new ConcurrentDictionary<string, CameraReachability>(StringComparer.Ordinal);

      if (toProbe.Count > 0)
      {
        await Parallel.ForEachAsync(
          toProbe,
          new ParallelOptions { MaxDegreeOfParallelism = ProbeParallelism, CancellationToken = ct },
          async (camera, token) =>
          {
            try
            {
              probes[camera.CameraId] = await reachability.ProbeAsync(camera, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
              throw;
            }
            catch (Exception ex)
            {
              // The probe already answers "unknown" for its own failures; this is a last resort.
              logger.LogDebug(ex, "Reachability probe failed for camera '{CameraId}'", camera.CameraId);
              probes[camera.CameraId] = CameraReachability.Unknown;
            }
          });
      }

      var candidates = new List<PresenceCandidate>(cameras.Count);
      foreach (var camera in cameras)
      {
        var decision = Decide(camera, summaries[camera.CameraId], probes);
        if (decision.Connected is not null || decision.NoVideo is not null)
          candidates.Add(new PresenceCandidate(camera, decision));
      }

      return candidates;
    }

    private static PresenceDecision Decide(
      CameraInfo camera,
      CameraVideoSummary video,
      IReadOnlyDictionary<string, CameraReachability> probes)
    {
      // A disabled camera does not run its streams at all: clear the video state, leave the
      // connection to whatever it last was.
      if (camera.Disabled)
        return new PresenceDecision(Connected: null, NoVideo: false);

      var reachability = !camera.HasOnvif
        ? CameraReachability.NoOnvif
        : probes.TryGetValue(camera.CameraId, out var probed)
          ? probed
          : CameraReachability.Unknown; // not asked: nothing is known either way

      return CameraPresenceRules.Decide(video, reachability);
    }
  }
}
