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
}

public record SetMonitorCamerasBody
{
  public IReadOnlyList<string> CameraIds { get; init; } = [];
}

public record SetMonitorSlotBody
{
  public string CameraId { get; init; } = string.Empty;
}
