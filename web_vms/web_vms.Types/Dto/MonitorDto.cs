namespace web_vms.Types.Dto;

public record MonitorSlotDto
{
  public int SlotIndex { get; init; }
  public string CameraId { get; init; } = string.Empty;
  /// <summary>
  /// Which stream this slot is pinned to for the caller; empty means the tile picks one from its
  /// own size. Unlike <see cref="CameraId"/> this is per operator — a shared wall is watched by
  /// several people, and one of them pinning a stream must not change what the others see.
  /// </summary>
  public string StreamName { get; init; } = string.Empty;
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

/// <summary>Empty <see cref="StreamName"/> releases the pin, back to automatic selection.</summary>
public record SetMonitorSlotStreamBody
{
  public string StreamName { get; init; } = string.Empty;
}
