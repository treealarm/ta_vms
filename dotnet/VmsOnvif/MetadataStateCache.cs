using System.Collections.Concurrent;

namespace VmsOnvif;

/// <summary>
/// Answers "is this camera configured to stream object boxes right now?" without making the caller
/// wait on the camera.
/// </summary>
/// <remarks>
/// The monitor asks this for every live slot to decide whether to light the "show boxes" button, so
/// a synchronous probe would put a round of SOAP round-trips in front of the grid rendering — and
/// for cameras that are offline, a timeout each. Instead reads are served from this cache and a
/// miss only schedules a background probe; the caller re-asks later and gets the answer then.
///
/// Negative results expire faster than positive ones: "false" is most often an unreachable camera,
/// and re-probing that every minute is cheap, whereas a camera that does stream boxes will not stop
/// doing so on its own — the only thing that changes it is a write through OnvifSettingsService,
/// which invalidates the entry directly.
///
/// Safe as a process-wide cache because VmsOnvif is single-instance per node by contract.
/// </remarks>
public sealed class MetadataStateCache(ILogger<MetadataStateCache> logger)
{
  private sealed record Entry(bool BboxCapable, DateTime At);

  private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(1);

  private readonly ConcurrentDictionary<string, Entry> _entries = new();
  private readonly ConcurrentDictionary<string, byte> _inFlight = new();

  /// <summary>The cached answer, or null when nothing fresh is known.</summary>
  public bool? TryGet(string cameraId)
  {
    if (!_entries.TryGetValue(cameraId, out var entry))
      return null;

    var ttl = entry.BboxCapable ? PositiveTtl : NegativeTtl;
    if (DateTime.UtcNow - entry.At >= ttl)
      return null;

    return entry.BboxCapable;
  }

  public void Set(string cameraId, bool bboxCapable)
    => _entries[cameraId] = new Entry(bboxCapable, DateTime.UtcNow);

  public void Invalidate(string cameraId) => _entries.TryRemove(cameraId, out _);

  /// <summary>
  /// Starts a probe for a camera whose answer is unknown, unless one is already running. Returns
  /// immediately; the result lands in the cache for the next read.
  /// </summary>
  public void ProbeInBackground(string cameraId, Func<CancellationToken, Task<bool>> probe)
  {
    if (!_inFlight.TryAdd(cameraId, 0))
      return;

    _ = Task.Run(async () =>
    {
      try
      {
        // Bounded on its own rather than borrowing the request's token: the request that triggered
        // this probe is long gone by the time it finishes, and that is the point.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Set(cameraId, await probe(cts.Token));
      }
      catch (Exception ex)
      {
        // A camera that cannot answer cannot be streaming boxes either. Cached as a negative so a
        // grid full of offline cameras does not re-probe on every refresh.
        logger.LogDebug(ex, "Metadata state probe failed for camera {CameraId}", cameraId);
        Set(cameraId, false);
      }
      finally
      {
        _inFlight.TryRemove(cameraId, out _);
      }
    });
  }
}
