using web_vms.Types.Dto;

namespace web_vms
{
  public interface ICameraManager
  {
    Task<bool> Add(CameraStateDto dto, CancellationToken ct = default);
    Task<bool> Remove(string id, CancellationToken ct = default);
    Task<List<CameraStateDto>> List(CancellationToken ct = default);
    Task<List<MediaServerNodeDto>> ListServers(CancellationToken ct = default);
    Task<bool> SetState(CameraStateDto dto, CancellationToken ct = default);
    Task<CameraStateDto?> Get(string id, CancellationToken ct = default);
  }
}
