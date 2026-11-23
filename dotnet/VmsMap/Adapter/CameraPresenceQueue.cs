using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VmsMap.Adapter
{
  // Cameras whose presence must be re-read, deduplicated. Both the video event and a device event or
  // snapshot feed it; the drain in CameraPresenceSubscriber empties it once a second. A camera that
  // moved twice before being read is read once, with the state it has by then.
  internal sealed class CameraPresenceQueue
  {
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);

    public void Enqueue(string? cameraId)
    {
      if (!string.IsNullOrWhiteSpace(cameraId))
        _dirty[cameraId] = 0;
    }

    public IReadOnlyList<string> TakeAll()
    {
      var batch = new List<string>();
      foreach (var cameraId in _dirty.Keys)
      {
        if (_dirty.TryRemove(cameraId, out _))
          batch.Add(cameraId);
      }
      return batch;
    }
  }
}
