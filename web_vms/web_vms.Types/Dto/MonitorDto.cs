namespace web_vms.Types.Dto;

public record MonitorSlotDto
{
  public int SlotIndex { get; init; }
  public string CameraId { get; init; } = string.Empty;
}

public record MonitorDto
{
  public string MonitorId { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public string Layout { get; init; } = "2x2";
  public IReadOnlyList<string> CameraIds { get; init; } = [];
  public IReadOnlyList<MonitorSlotDto> Slots { get; init; } = [];
  /// <summary>"wall" (shared video wall) or "investigation" (private board).</summary>
  public string Kind { get; init; } = "wall";
  /// <summary>
  /// Owner's username, read-only for clients: on writes the server overwrites it with the caller's
  /// identity, so a request body cannot claim somebody else's board.
  /// </summary>
  public string Owner { get; init; } = string.Empty;
}

public record CopyMonitorBody
{
  public string Username { get; init; } = string.Empty;
}

public record SetMonitorCamerasBody
{
  public IReadOnlyList<string> CameraIds { get; init; } = [];
}

public record SetMonitorSlotBody
{
  public string CameraId { get; init; } = string.Empty;
}
