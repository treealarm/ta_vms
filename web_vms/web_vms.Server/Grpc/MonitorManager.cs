using Grpc.Core;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms;

public class MonitorManager(CfgService.CfgServiceClient cfgClient)
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  public async Task<List<MonitorDto>> List(CancellationToken ct = default)
  {
    var response = await cfgClient.ListMonitorsAsync(new ListMonitorsRequest(), CfgOptions(ct));
    return response.Monitors.Select(ToDto).ToList();
  }

  public async Task<MonitorDto> Upsert(MonitorDto dto, CancellationToken ct = default)
  {
    var result = await cfgClient.UpsertMonitorAsync(ToProto(dto), CfgOptions(ct));
    return ToDto(result);
  }

  public async Task<bool> Remove(string id, CancellationToken ct = default)
  {
    var response = await cfgClient.RemoveMonitorAsync(new SourceRequest { SourceId = id }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> SetCameras(string monitorId, IEnumerable<string> cameraIds, CancellationToken ct = default)
  {
    var request = new SetMonitorCamerasRequest { MonitorId = monitorId };
    request.CameraIds.AddRange(cameraIds);
    var response = await cfgClient.SetMonitorCamerasAsync(request, CfgOptions(ct));
    return response.Success;
  }

  public async Task<bool> SetSlot(string monitorId, int slotIndex, string cameraId, CancellationToken ct = default)
  {
    var response = await cfgClient.SetMonitorSlotAsync(
      new SetMonitorSlotRequest { MonitorId = monitorId, SlotIndex = slotIndex, CameraId = cameraId },
      CfgOptions(ct));
    return response.Success;
  }

  private static MonitorDto ToDto(MonitorSetup m) => new()
  {
    MonitorId = m.MonitorId,
    Name      = m.Name,
    Layout    = m.Layout,
    CameraIds = m.CameraIds.ToList(),
    Slots     = m.Slots.Select(s => new MonitorSlotDto { SlotIndex = s.SlotIndex, CameraId = s.CameraId }).ToList(),
  };

  private static MonitorSetup ToProto(MonitorDto dto)
  {
    var proto = new MonitorSetup
    {
      MonitorId = dto.MonitorId ?? string.Empty,
      Name      = dto.Name,
      Layout    = dto.Layout,
    };
    proto.CameraIds.AddRange(dto.CameraIds);
    proto.Slots.AddRange(dto.Slots.Select(s => new MonitorSlot { SlotIndex = s.SlotIndex, CameraId = s.CameraId }));
    return proto;
  }
}
