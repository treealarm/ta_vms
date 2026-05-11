using OnvifLib;
using Vms.Cfg;

namespace VmsOnvif;

public static class OnvifUtils
{
  public static async Task<List<OnvifProfileInfo>> GetProfiles(CameraSetup camInfo)
  {
    if (camInfo?.OnvifConn == null)
      return [];

    var cam = Camera.Create(
      camInfo.OnvifConn.Ip,
      camInfo.OnvifConn.Port,
      camInfo.Cred?.User ?? string.Empty,
      camInfo.Cred?.Pass ?? string.Empty);

    var mediaService = await cam.GetMediaService();
    return mediaService?.GetProfiles() ?? [];
  }

  public static async Task<string> GetStreamUri(CameraSetup camInfo, string profileToken)
  {
    if (camInfo?.OnvifConn == null)
      return string.Empty;

    var cam = Camera.Create(
      camInfo.OnvifConn.Ip,
      camInfo.OnvifConn.Port,
      camInfo.Cred?.User ?? string.Empty,
      camInfo.Cred?.Pass ?? string.Empty);

    var mediaService = await cam.GetMediaService();
    if (mediaService == null)
      return string.Empty;

    if (string.IsNullOrEmpty(profileToken))
      profileToken = mediaService.GetProfiles().FirstOrDefault()?.Token ?? string.Empty;

    if (string.IsNullOrEmpty(profileToken))
      return string.Empty;

    return await mediaService.GetStreamUri(profileToken);
  }
}
