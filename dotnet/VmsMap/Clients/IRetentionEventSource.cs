using System;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Clients
{
  // One retention episode as VmsFs stores it: when it started, when its projected end is, which
  // stream and ONVIF trigger raised it, the episode's own id, and whether it is an alarm to the
  // map's operator at all. False keeps the recording and the clip list but raises nothing.
  public sealed record RetentionEpisode(
    DateTime AlarmStartTs,
    DateTime? AlarmEndTs,
    string? StreamName,
    string? Trigger,
    string EventUuid,
    bool RaiseAlarm = true);

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
