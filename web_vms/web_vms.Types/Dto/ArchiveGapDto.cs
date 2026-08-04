namespace web_vms.Types.Dto;

/// <summary>
/// One recorded interval during which a camera's source was unavailable.
/// </summary>
/// <remarks>
/// Not every outage is a hole in the archive: only those with <see cref="RecordingExpected"/> set
/// were supposed to be recorded. The rest are camera-health history and are shown as such.
/// </remarks>
public sealed class SourceOutageDto
{
  public string OutageUid { get; set; } = string.Empty;
  public string CameraId { get; set; } = string.Empty;
  public string? StreamName { get; set; }
  public DateTime StartedTs { get; set; }
  /// <summary>Null = the source is unavailable right now.</summary>
  public DateTime? EndedTs { get; set; }
  /// <summary><c>link_lost</c> or <c>server_down</c>.</summary>
  public string Cause { get; set; } = string.Empty;
  public bool RecordingExpected { get; set; }
  /// <summary><c>open|filling|filled|exhausted|partial|held|ignored</c>.</summary>
  public string State { get; set; } = string.Empty;
  /// <summary>How much of the interval is covered by clips. Display-only cache, may lag reality.</summary>
  public long FilledMs { get; set; }
  public int Attempts { get; set; }
  public string Message { get; set; } = string.Empty;
}
