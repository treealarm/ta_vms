using OnvifLib;
using Vms.Cfg;

namespace VmsOnvif;

public static class OnvifUtils
{
  public static async Task<string> GetStreamUri(CameraState camInfo)
  {
    if (camInfo?.OnvifConn == null)
    {
      return string.Empty;
    }

    var cam = Camera.Create(
      camInfo.OnvifConn.Ip,
      camInfo.OnvifConn.Port,
      camInfo.Cred?.User ?? string.Empty,
      camInfo.Cred?.Pass ?? string.Empty);

    var mediaService = await cam.GetMediaService();
    if (mediaService == null)
    {
      return string.Empty;
    }

    var profile = mediaService.GetProfiles().FirstOrDefault();
    if (profile == null)
    {
      return string.Empty;
    }

    return await mediaService.GetStreamUri(profile);
  }
}
