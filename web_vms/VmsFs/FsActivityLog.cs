namespace VmsFs;

public sealed record FsActivityEntry(
  Guid     ClipUid,
  string   FilePath,
  string   CameraId,
  string   StreamName,
  bool     Retained,
  long     SizeBytes,
  DateTime OccurredAt,
  string   EventType,  // "ingested" | "deleted"
  DateTime? EndTs);

public sealed class FsActivityLog
{
  private const int Max = 200;
  private readonly object _lock = new();
  private readonly Queue<FsActivityEntry> _queue = new(Max);

  public void Record(FsActivityEntry entry)
  {
    lock (_lock)
    {
      if (_queue.Count >= Max)
        _queue.Dequeue();
      _queue.Enqueue(entry);
    }
  }

  public IReadOnlyList<FsActivityEntry> GetRecent(int limit)
  {
    lock (_lock)
      return _queue.TakeLast(Math.Clamp(limit, 1, Max)).ToList();
  }
}
