namespace VmsSquareSingleton;

/// <summary>
/// What this process knows about its link to Square, as the call sites have found it.
/// </summary>
/// <remarks>
/// In memory only and not shared: this service is a singleton by contract, so the one copy is the
/// whole truth, and a restart honestly starts again from "not synced yet".
/// </remarks>
public sealed class SquareLinkState
{
  private readonly Lock _gate = new();
  private bool _synced;
  private DateTime? _lastOkAt;
  private DateTime? _lastErrorAt;
  private string _lastError = string.Empty;

  /// <summary>The whole camera tree reached Square. Latches: a tree once pushed stays pushed.</summary>
  public void MarkSynced()
  {
    lock (_gate)
    {
      _synced = true;
      _lastOkAt = DateTime.UtcNow;
      _lastError = string.Empty;
      _lastErrorAt = null;
    }
  }

  public void RecordSuccess()
  {
    lock (_gate)
      _lastOkAt = DateTime.UtcNow;
  }

  public void RecordFailure(Exception ex)
  {
    lock (_gate)
    {
      _lastError = ex.Message;
      _lastErrorAt = DateTime.UtcNow;
    }
  }

  public (bool Synced, DateTime? LastOkAt, DateTime? LastErrorAt, string LastError) Snapshot()
  {
    lock (_gate)
      return (_synced, _lastOkAt, _lastErrorAt, _lastError);
  }
}
