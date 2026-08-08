using System.Collections.Concurrent;
using OnvifLib;

namespace VmsOnvif;

/// <summary>
/// The camera clock ↔ server clock correction for everything Profile G.
/// </summary>
/// <remarks>
/// The device indexes its SD card in its own clock, which can be off by hours. Asking it for a
/// window in our clock returns the wrong footage — silently, since the answer looks perfectly
/// well-formed. So this is the boundary: every timestamp leaving VmsOnvif is in server time, every
/// timestamp handed to the device is in camera time, and nothing outside this service ever sees the
/// camera's scale.
///
/// The import path in media_server needs no correction of its own: its RTSP seek is expressed as
/// <c>start_ts − recording_start_ts</c>, a difference, so a constant offset cancels out as long as
/// both ends were converted the same way — and the clip then gets stamped in our time by
/// construction.
///
/// What this cannot repair is a camera whose clock *jumped* after recording: material written
/// before the jump carries the old scale, and no single offset is right for the whole card.
/// </remarks>
public sealed partial class OnvifSettingsService
{
  private readonly ConcurrentDictionary<string, (TimeSpan Offset, DateTime At)> _clockOffsets = new();

  // Same TTL as the interval cache, and for the same reason: one browse session must see one
  // consistent correction, or its listed intervals and its replay window would disagree by the
  // measurement jitter.
  private static readonly TimeSpan ClockOffsetTtl = TimeSpan.FromSeconds(60);

  /// <summary>
  /// camera − server, or null when the camera does not report its time. Never silently zero: an
  /// unknown offset has to reach the caller, which decides whether it can proceed (browsing can,
  /// pulling footage cannot).
  /// </summary>
  public async Task<TimeSpan?> GetClockOffsetAsync(string cameraId, CancellationToken ct)
  {
    if (_clockOffsets.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.At < ClockOffsetTtl)
      return cached.Offset;

    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return null;

    try
    {
      var reading = await GetOrCreate(data.Conn).MeasureClockAsync();
      if (reading == null)
      {
        logger.LogWarning("Camera {CameraId} did not report its UTC time; edge archive times cannot be corrected", cameraId);
        return null;
      }

      var offset = reading.Offset;
      if (Math.Abs(offset.TotalSeconds) >= 5)
      {
        logger.LogInformation(
          "Camera {CameraId} clock is {Offset} away from ours (round trip {RoundTrip}ms)",
          cameraId, offset, (int)reading.RoundTrip.TotalMilliseconds);
      }

      _clockOffsets[cameraId] = (offset, DateTime.UtcNow);
      return offset;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Clock offset measurement failed for camera {CameraId}", cameraId);
      return null;
    }
  }

  /// <summary>Server time → camera time, for anything handed to the device.</summary>
  private static DateTime? ToCameraTime(DateTime? serverTs, TimeSpan? offset) =>
    serverTs is { } ts && offset is { } off ? ts + off : serverTs;

  /// <summary>Camera time → server time, for anything the device tells us.</summary>
  private static DateTime? ToServerTime(DateTime? cameraTs, TimeSpan? offset) =>
    cameraTs is { } ts && offset is { } off ? ts - off : cameraTs;

  private static OnvifEdgeRecordingSummary? ToServerTime(OnvifEdgeRecordingSummary? summary, TimeSpan? offset) =>
    summary == null || offset == null
      ? summary
      : summary with
      {
        DataFrom = ToServerTime(summary.DataFrom, offset),
        DataUntil = ToServerTime(summary.DataUntil, offset),
      };

  private static OnvifEdgeRecording ToServerTime(OnvifEdgeRecording recording, TimeSpan? offset) =>
    offset == null
      ? recording
      : recording with
      {
        EarliestRecording = ToServerTime(recording.EarliestRecording, offset),
        LatestRecording = ToServerTime(recording.LatestRecording, offset),
        Tracks = recording.Tracks.Select(t => t with
        {
          DataFrom = ToServerTime(t.DataFrom, offset),
          DataTo = ToServerTime(t.DataTo, offset),
        }).ToList(),
      };

  private static List<OnvifEdgeInterval> ToServerTime(
    IEnumerable<OnvifEdgeInterval> intervals, TimeSpan? offset) =>
    offset is not { } off
      ? intervals.ToList()
      : intervals.Select(i => i with
      {
        From = i.From - off,
        Until = i.Until is { } until ? until - off : null,
      }).ToList();
}
