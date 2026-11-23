using System;
using System.Collections.Generic;

namespace VmsFs;

/// <summary>
/// The video state of every stream, derived from the heartbeats.
/// </summary>
/// <remarks>
/// Held apart from <see cref="SourceLivenessTracker"/> deliberately: that one writes outage
/// intervals to the database, and a failing write must not lose what is known about presence.
/// Nothing here touches the database or the bus; a single lock guards the whole map, which is
/// cheap at the heartbeat cadence.
///
/// Silence is judged by the caller, not here: only the sweeper knows whether the whole node went
/// quiet (the media server stopped reporting) or a single stream did (that stream stopped while the
/// rest of the node kept going). The former says nothing about the cameras.
/// </remarks>
public sealed class SourceVideoTracker
{
  public enum VideoState
  {
    Unknown,
    Alive,
    NoVideo,
  }

  public readonly record struct VideoSnapshot(string CameraId, string StreamName, VideoState State);

  private sealed record Entry(
    VideoState State, DateTime LastReportUtc, DateTime RunStartedUtc, string InstanceId);

  private readonly Dictionary<(string Camera, string Stream), Entry> _entries = new();
  private readonly object _gate = new();

  public static (string Camera, string Stream) Key(string cameraId, string streamName) =>
    (cameraId, streamName ?? string.Empty);

  /// <summary>
  /// Applies one heartbeat and answers whether the stream became a state a consumer can act on
  /// (<c>Alive</c> or <c>NoVideo</c>). A transition into <c>Unknown</c> is not worth announcing.
  /// </summary>
  public bool Observe(
    string cameraId, string streamName, bool alive, string? instanceId, DateTime nowUtc)
  {
    var key = Key(cameraId, streamName);
    lock (_gate)
    {
      _entries.TryGetValue(key, out var previous);

      var newInstance = instanceId ?? string.Empty;
      // An empty id from an older producer must not reset the run on every heartbeat.
      var instanceChanged =
        previous is not null
        && !string.IsNullOrEmpty(newInstance)
        && !string.IsNullOrEmpty(previous.InstanceId)
        && !string.Equals(previous.InstanceId, newInstance, StringComparison.Ordinal);
      var runStarted = previous is null || instanceChanged ? nowUtc : previous.RunStartedUtc;

      VideoState next;
      if (alive)
      {
        next = VideoState.Alive;
      }
      else if (previous is not null && !instanceChanged && previous.State == VideoState.Alive)
      {
        // A dead report after a live run: video_srv already waited out its five-second watchdog,
        // and another Grace would show the operator twenty-odd seconds of silence.
        next = VideoState.NoVideo;
      }
      else if (nowUtc - runStarted >= SourceLivenessTracker.Grace)
      {
        // Dead from the start of this run and still dead after the startup allowance.
        next = VideoState.NoVideo;
      }
      else
      {
        next = VideoState.Unknown;
      }

      var changed = previous is null
        ? next != VideoState.Unknown
        : next != previous.State;

      _entries[key] = new Entry(
        next,
        nowUtc,
        runStarted,
        string.IsNullOrEmpty(newInstance) ? previous?.InstanceId ?? string.Empty : newInstance);

      return changed && next != VideoState.Unknown;
    }
  }

  /// <summary>
  /// Applies the silence the sweeper judged, and returns the streams that became <c>NoVideo</c>.
  /// </summary>
  /// <param name="silent">Streams the sweeper found without a fresh report.</param>
  /// <param name="wholeNodeSilent">
  /// Every expected stream of the node is silent: that is the media server, not the cameras, so the
  /// video state becomes <c>Unknown</c> rather than <c>NoVideo</c>.
  /// </param>
  public IReadOnlyList<VideoSnapshot> Decay(
    IReadOnlySet<(string Camera, string Stream)> silent, bool wholeNodeSilent, DateTime nowUtc)
  {
    var changed = new List<VideoSnapshot>();
    lock (_gate)
    {
      foreach (var key in silent)
      {
        if (!_entries.TryGetValue(key, out var entry))
          continue; // never heard: absent, not dead
        if (nowUtc - entry.LastReportUtc < SourceLivenessTracker.Stale)
          continue; // a report arrived while the sweeper was building its list

        var next = wholeNodeSilent ? VideoState.Unknown : VideoState.NoVideo;
        if (next == entry.State)
          continue;

        _entries[key] = entry with { State = next };
        if (next == VideoState.NoVideo)
          changed.Add(new VideoSnapshot(key.Camera, key.Stream, next));
      }
    }
    return changed;
  }

  /// <summary>
  /// The known states among the asked cameras (all of them when the list is empty). A stream that
  /// has never been heard is left out, and so is a stale "alive"; a NoVideo survives staleness.
  /// </summary>
  public IReadOnlyList<VideoSnapshot> Snapshot(
    IReadOnlyCollection<string>? cameraIds, DateTime nowUtc)
  {
    HashSet<string>? filter = cameraIds is { Count: > 0 }
      ? new HashSet<string>(cameraIds, StringComparer.Ordinal)
      : null;

    var result = new List<VideoSnapshot>();
    lock (_gate)
    {
      foreach (var ((camera, stream), entry) in _entries)
      {
        if (filter is not null && !filter.Contains(camera))
          continue;
        // A stale "alive" is not a state — nobody has confirmed a frame for a whole Stale window.
        // A NoVideo survives staleness: it is the absence verdict itself, and a stream that has
        // stopped reporting altogether is not thereby delivering video again. Whether that silence
        // is the whole node's is the sweeper's call, which overwrites the entry with Unknown.
        if (nowUtc - entry.LastReportUtc >= SourceLivenessTracker.Stale
            && entry.State == VideoState.Alive)
        {
          continue;
        }
        if (entry.State == VideoState.Unknown)
          continue;
        result.Add(new VideoSnapshot(camera, stream, entry.State));
      }
    }
    return result;
  }

  /// <summary>Drops streams that left the configuration. Nothing is announced: the tree follows config.</summary>
  public void PruneTo(IReadOnlySet<(string Camera, string Stream)> expected)
  {
    lock (_gate)
    {
      var gone = new List<(string Camera, string Stream)>();
      foreach (var key in _entries.Keys)
      {
        if (!expected.Contains(key))
          gone.Add(key);
      }
      foreach (var key in gone)
        _entries.Remove(key);
    }
  }

  public void Forget(string cameraId, string streamName)
  {
    lock (_gate)
      _entries.Remove(Key(cameraId, streamName));
  }
}
