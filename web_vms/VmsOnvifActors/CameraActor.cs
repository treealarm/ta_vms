using Dapr.Actors.Runtime;
using OnvifLib;
using web_vms.Types.Actors;

namespace VmsOnvifActors;

public class CameraActor(ActorHost host) : Actor(host), ICameraActor
{
  private Camera? _camera;
  private string _cameraKey = string.Empty;

  public async Task<List<ProfileInfoDto>> GetProfilesAsync(CameraConnInfo conn)
  {
    var camera = EnsureCamera(conn);
    var mediaService = await camera.GetMediaService();

    if (mediaService == null)
    {
      Evict();
      return [];
    }

    return mediaService.GetProfiles()
      .Select(p => new ProfileInfoDto(p.Token, p.Name, p.Width, p.Height, p.Encoding))
      .ToList();
  }

  public async Task<GetStreamUriActorResult> GetStreamUriAsync(GetStreamUriActorRequest request)
  {
    var camera = EnsureCamera(request.Conn);
    var mediaService = await camera.GetMediaService();

    if (mediaService == null)
    {
      Evict();
      return new GetStreamUriActorResult(string.Empty);
    }

    var token = request.ProfileToken;
    if (string.IsNullOrEmpty(token))
      token = mediaService.GetProfiles().FirstOrDefault()?.Token ?? string.Empty;

    if (string.IsNullOrEmpty(token))
      return new GetStreamUriActorResult(string.Empty);

    return new GetStreamUriActorResult(await mediaService.GetStreamUri(token));
  }

  protected override Task OnDeactivateAsync()
  {
    Evict();
    return base.OnDeactivateAsync();
  }

  private Camera EnsureCamera(CameraConnInfo conn)
  {
    var key = $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}";
    if (_camera is null || _cameraKey != key)
    {
      _camera = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass);
      _cameraKey = key;
    }
    return _camera;
  }

  private void Evict()
  {
    _camera = null;
    _cameraKey = string.Empty;
  }
}
