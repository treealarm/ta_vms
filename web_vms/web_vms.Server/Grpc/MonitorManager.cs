using Grpc.Core;
using Vms.Cfg;
using web_vms.Types.Dto;

namespace web_vms;

public class MonitorManager(CfgService.CfgServiceClient cfgClient)
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct = default) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  // Every call takes the caller's username: VmsCfg applies it in SQL, so private boards are
  // filtered and guarded at the storage layer rather than here. An empty owner is legitimate —
  // it is what standalone (no Keycloak) deployments use for everything.
  public async Task<List<MonitorDto>> List(string owner, CancellationToken ct = default)
  {
    var response = await cfgClient.ListMonitorsAsync(new ListMonitorsRequest { Owner = owner }, CfgOptions(ct));
    return response.Monitors.Select(ToDto).ToList();
  }

  public async Task<MonitorDto> Upsert(MonitorDto dto, string owner, CancellationToken ct = default)
  {
    var result = await cfgClient.UpsertMonitorAsync(ToProto(dto, owner), CfgOptions(ct));
    return ToDto(result);
  }

  public async Task<bool> Remove(string id, string owner, CancellationToken ct = default)
  {
    var response = await cfgClient.RemoveMonitorAsync(
      new RemoveMonitorRequest { MonitorId = id, Owner = owner }, CfgOptions(ct));
    return response.Success;
  }

  public async Task<ApiResponse> SetCameras(string monitorId, IEnumerable<string> cameraIds, string owner, CancellationToken ct = default)
  {
    var request = new SetMonitorCamerasRequest { MonitorId = monitorId, Owner = owner };
    request.CameraIds.AddRange(cameraIds);
    var response = await cfgClient.SetMonitorCamerasAsync(request, CfgOptions(ct));
    return new ApiResponse(response.Success, response.Message);
  }

  public async Task<ApiResponse> SetSlot(string monitorId, int slotIndex, string cameraId, string owner, CancellationToken ct = default)
  {
    var response = await cfgClient.SetMonitorSlotAsync(
      new SetMonitorSlotRequest { MonitorId = monitorId, SlotIndex = slotIndex, CameraId = cameraId, Owner = owner },
      CfgOptions(ct));
    return new ApiResponse(response.Success, response.Message);
  }

  public async Task<ApiResponse> SetSlotStream(string monitorId, int slotIndex, string streamName, string owner, CancellationToken ct = default)
  {
    var response = await cfgClient.SetMonitorSlotStreamAsync(
      new SetMonitorSlotStreamRequest { MonitorId = monitorId, SlotIndex = slotIndex, StreamName = streamName, Owner = owner },
      CfgOptions(ct));
    return new ApiResponse(response.Success, response.Message);
  }

  public async Task<ApiResponse> ClearSlot(string monitorId, int slotIndex, string owner, CancellationToken ct = default)
  {
    var response = await cfgClient.ClearMonitorSlotAsync(
      new ClearMonitorSlotRequest { MonitorId = monitorId, SlotIndex = slotIndex, Owner = owner },
      CfgOptions(ct));
    return new ApiResponse(response.Success, response.Message);
  }

  public async Task<MonitorDto> CopyTo(string monitorId, string owner, string targetOwner, CancellationToken ct = default)
  {
    var result = await cfgClient.CopyMonitorAsync(
      new CopyMonitorRequest { MonitorId = monitorId, Owner = owner, TargetOwner = targetOwner },
      CfgOptions(ct));
    return ToDto(result);
  }

  private static MonitorDto ToDto(MonitorSetup m) => new()
  {
    MonitorId = m.MonitorId,
    Name      = m.Name,
    Layout    = m.Layout,
    Kind      = string.IsNullOrEmpty(m.Kind) ? MonitorKinds.Wall : m.Kind,
    Owner     = m.Owner,
    CameraIds = m.CameraIds.ToList(),
    Slots     = m.Slots.Select(s => new MonitorSlotDto
    {
      SlotIndex  = s.SlotIndex,
      CameraId   = s.CameraId,
      StreamName = s.StreamName,
    }).ToList(),
  };

  private static MonitorSetup ToProto(MonitorDto dto, string owner)
  {
    var proto = new MonitorSetup
    {
      MonitorId = dto.MonitorId ?? string.Empty,
      Name      = dto.Name,
      Layout    = dto.Layout,
      Kind      = dto.Kind == MonitorKinds.Investigation ? MonitorKinds.Investigation : MonitorKinds.Wall,
      // Never dto.Owner: the body is client-supplied and must not be able to name an owner.
      Owner     = owner,
    };
    proto.CameraIds.AddRange(dto.CameraIds);
    proto.Slots.AddRange(dto.Slots.Select(s => new MonitorSlot { SlotIndex = s.SlotIndex, CameraId = s.CameraId }));
    return proto;
  }
}

public static class MonitorKinds
{
  public const string Wall = "wall";
  public const string Investigation = "investigation";
}
