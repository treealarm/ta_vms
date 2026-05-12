using web_vms.Types.Dto;

namespace web_vms
{
  public interface ICameraManager
  {
    Task<bool> Add(CameraSetupDto dto, CancellationToken ct = default);
    Task<bool> Remove(string id, CancellationToken ct = default);
    Task<List<CameraSetupDto>> List(CancellationToken ct = default);
    Task<List<MediaServerNodeDto>> ListServers(CancellationToken ct = default);
    Task<bool> SetSetup(CameraSetupDto dto, CancellationToken ct = default);
    Task<CameraSetupDto?> Get(string id, CancellationToken ct = default);
  }
}
