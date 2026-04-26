using web_vms.Types.Dto;

namespace web_vms
{
  public interface ICameraManager
  {
    Task<bool> Add(CameraStateDto dto);
    Task<bool> Remove(string id);
    Task<List<CameraStateDto>> List();
    Task<List<MediaServerNodeDto>> ListServers();
    Task<bool> SetState(CameraStateDto dto);
    Task<CameraStateDto?> Get(string id);
  }
}
