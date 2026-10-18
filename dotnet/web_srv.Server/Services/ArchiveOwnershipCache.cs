using System.Collections.Concurrent;
using Vms.Fs;

namespace web_srv;

/// <summary>
/// Remembers which camera a clip or episode belongs to, so the segment endpoint can apply the
/// archive permission without a lookup per segment.
///
/// Playlists are built from <see cref="Recording"/>s that already carry <c>camera_id</c>, so
/// priming costs nothing and the mapping is always in place before hls.js asks for the first
/// segment. A miss (deep link, restart, eviction) falls back to <c>GetRecordingByUid</c>.
/// </summary>
public sealed class ArchiveOwnershipCache
{
  // Bounded by generation swap rather than per-entry eviction: entries are immutable facts, so
  // dropping a generation only costs a re-lookup on the rare miss.
  private const int MaxEntriesPerGeneration = 50_000;

  private ConcurrentDictionary<string, string> _current = new(StringComparer.Ordinal);
  private ConcurrentDictionary<string, string> _previous = new(StringComparer.Ordinal);
  private readonly object _rotateLock = new();

  public void PrimeClips(IEnumerable<Recording> recordings)
  {
    foreach (var recording in recordings)
    {
      if (!string.IsNullOrEmpty(recording.ClipUid) && !string.IsNullOrEmpty(recording.CameraId))
        Put(ClipKey(recording.ClipUid), recording.CameraId);
    }
  }

  public void PrimeEvent(string eventUuid, string cameraId)
  {
    if (!string.IsNullOrEmpty(eventUuid) && !string.IsNullOrEmpty(cameraId))
      Put(EventKey(eventUuid), cameraId);
  }

  public bool TryGetClipCamera(string clipUid, out string cameraId) => TryGet(ClipKey(clipUid), out cameraId);

  public bool TryGetEventCamera(string eventUuid, out string cameraId) => TryGet(EventKey(eventUuid), out cameraId);

  private static string ClipKey(string clipUid) => "c:" + clipUid;
  private static string EventKey(string eventUuid) => "e:" + eventUuid;

  private bool TryGet(string key, out string cameraId)
  {
    if (_current.TryGetValue(key, out var found) || _previous.TryGetValue(key, out found))
    {
      cameraId = found;
      return true;
    }

    cameraId = string.Empty;
    return false;
  }

  private void Put(string key, string cameraId)
  {
    _current[key] = cameraId;
    if (_current.Count < MaxEntriesPerGeneration)
      return;

    lock (_rotateLock)
    {
      if (_current.Count < MaxEntriesPerGeneration)
        return;

      _previous = _current;
      _current = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    }
  }
}
