using System;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // One retention episode as VmsFs stores it: when it started, when its projected end is, which
  // stream and ONVIF trigger raised it, and the episode's own id.
  public sealed record RetentionEpisode(
    DateTime AlarmStartTs,
    DateTime? AlarmEndTs,
    string? StreamName,
    string? Trigger,
    string EventUuid);

  public interface IRetentionEventSource
  {
    /// <summary>
    /// The episode that starts at <paramref name="alarmStartTs"/>. Null when VmsFs has not written
    /// it yet — the caller retries the Dapr message.
    /// </summary>
    Task<RetentionEpisode?> FindAsync(
      string cameraId, DateTime alarmStartTs, CancellationToken ct);
  }
}
