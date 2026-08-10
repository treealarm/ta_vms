namespace VmsAnalytics;

// In-memory, best-effort ring buffer of recent detection events for the debug panel — mirrors
// VmsFs.FsActivityLog. Never persisted: crops/bboxes here are debugging aids, the durable record
// is the detections table (which deliberately stores neither).
public sealed record AnalyticsActivityEntry(
  DateTime DetectedAt,
  string   CameraId,
  long     TrackId,
  string   Kind,
  float    Confidence,
  float    BboxX,
  float    BboxY,
  float    BboxWidth,
  float    BboxHeight,
  string?  PlateText,
  byte[]   CropJpeg,   // empty unless the watch has attach_debug_crops enabled
  bool     Persisted); // false = seen but dropped by debounce/armed check

public sealed class AnalyticsActivityLog
{
  private const int Max = 100;
  private readonly object _lock = new();
  private readonly Queue<AnalyticsActivityEntry> _queue = new(Max);

  public void Record(AnalyticsActivityEntry entry)
  {
    lock (_lock)
    {
      if (_queue.Count >= Max)
        _queue.Dequeue();
      _queue.Enqueue(entry);
    }
  }

  public IReadOnlyList<AnalyticsActivityEntry> GetRecent(int limit)
  {
    lock (_lock)
      return _queue.TakeLast(Math.Clamp(limit, 1, Max)).ToList();
  }
}
