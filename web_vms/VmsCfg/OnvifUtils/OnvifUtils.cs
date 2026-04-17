using System.Linq;
using OnvifLib;
using web_vms.Types.Dto;

namespace VmsCfg
{
  public static class OnvifUtils
  {
    public static async Task<string> GetStreamUri(CameraStateDto camInfo)
    {
      if (camInfo == null || camInfo.OnvifConn == null)
      {
        return string.Empty;
      }

      var cam = Camera.Create(
        camInfo.OnvifConn.ip,
        camInfo.OnvifConn.port,
        camInfo?.Cred?.User ?? string.Empty,
        camInfo?.Cred?.Pass ?? string.Empty);

      var mediaService = await cam.GetMediaService();
      if (mediaService != null)
      {
        var profile = mediaService.GetProfiles().FirstOrDefault();
        if (profile != null)
        {
          var uri = await mediaService.GetStreamUri(profile);
          return uri;
        }
      }

      return string.Empty;
    }
  }
}
