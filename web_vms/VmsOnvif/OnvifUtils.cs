using System.Collections.Concurrent;
using OnvifLib;
using Vms.Cfg;

namespace VmsOnvif;

public static class OnvifUtils
{
  private static readonly ConcurrentDictionary<string, Camera> _cameraCache = new();

  private static Camera GetOrCreateCamera(CameraSetup camInfo)
  {
    var user = camInfo.Cred?.User ?? string.Empty;
    var pass = camInfo.Cred?.Pass ?? string.Empty;
    var key  = $"{camInfo.OnvifConn!.Ip}:{camInfo.OnvifConn.Port}:{user}:{pass}";

    return _cameraCache.GetOrAdd(key, _ =>
      Camera.Create(camInfo.OnvifConn.Ip, camInfo.OnvifConn.Port, user, pass));
  }

  public static async Task<List<OnvifProfileInfo>> GetProfiles(CameraSetup camInfo)
  {
    if (camInfo?.OnvifConn == null)
      return [];

    var cam = GetOrCreateCamera(camInfo);
    var mediaService = await cam.GetMediaService();

    if (mediaService == null)
    {
      // Service failed — evict so next call re-discovers
      var user = camInfo.Cred?.User ?? string.Empty;
      var pass = camInfo.Cred?.Pass ?? string.Empty;
      _cameraCache.TryRemove($"{camInfo.OnvifConn.Ip}:{camInfo.OnvifConn.Port}:{user}:{pass}", out _);
    }

    return mediaService?.GetProfiles() ?? [];
  }

  public static async Task<string> GetStreamUri(CameraSetup camInfo, string profileToken)
  {
    if (camInfo?.OnvifConn == null)
      return string.Empty;

    var cam = GetOrCreateCamera(camInfo);
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
