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

  public async Task PtzContinuousMoveAsync(PtzMoveActorRequest request)
  {
    var ptz = await GetPtzOrThrow(request.Conn);
    await ptz.ContinuousMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom);
  }

  public async Task PtzAbsoluteMoveAsync(PtzAbsoluteMoveActorRequest request)
  {
    var ptz = await GetPtzOrThrow(request.Conn);
    var speed = request.Speed > 0 ? request.Speed : 0.5f;
    await ptz.AbsoluteMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom, speed, speed);
  }

  public async Task PtzRelativeMoveAsync(PtzMoveActorRequest request)
  {
    var ptz = await GetPtzOrThrow(request.Conn);
    var speed = 0.5f;
    await ptz.RelativeMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom, speed, speed);
  }

  public async Task PtzStopAsync(PtzStopActorRequest request)
  {
    var ptz = await GetPtzOrThrow(request.Conn);
    await ptz.StopAsync(request.ProfileToken);
  }

  public async Task<PtzCapabilitiesDto> GetPtzCapabilitiesAsync(CameraConnInfo conn)
  {
    var camera = EnsureCamera(conn);
    var ptz = await camera.GetPtzService();
    if (ptz == null)
      return new PtzCapabilitiesDto(false, false, false);

    var caps = await ptz.GetCapabilitiesAsync(string.Empty);
    return new PtzCapabilitiesDto(caps.AbsoluteMove, caps.RelativeMove, caps.ContinuousMove);
  }

  private async Task<PtzService2> GetPtzOrThrow(CameraConnInfo conn)
  {
    var camera = EnsureCamera(conn);
    var ptz = await camera.GetPtzService();
    if (ptz == null)
    {
      Evict();
      throw new InvalidOperationException($"PTZ service not available for {conn.Ip}:{conn.Port}");
    }
    return ptz;
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
      _camera = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass, logger: new OnvifLoggerAdapter(Logger));
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
