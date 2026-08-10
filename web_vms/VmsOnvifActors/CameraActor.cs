using Dapr.Actors.Runtime;
using OnvifLib;
using web_vms.Types.Actors;

namespace VmsOnvifActors;

/// The node this actor host serves. Injected so the actor can reject a camera from another node
/// rather than time out trying to reach it.
public sealed record CameraActorDomain(string OwnServerId);

public class CameraActor(ActorHost host, CameraActorDomain domain) : Actor(host), ICameraActor
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
      .Select(p => new ProfileInfoDto(p.Token, p.Name, p.Width, p.Height, p.Encoding, p.VideoSourceToken))
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

  public async Task<SnapshotActorResult> GetSnapshotAsync(CameraConnInfo conn)
  {
    var camera = EnsureCamera(conn);
    var mediaService = await camera.GetMediaService();
    if (mediaService == null)
    {
      // Service-resolution failure (auth/connectivity) — the cached Camera is no good, discard it.
      Evict();
      return new SnapshotActorResult([], null);
    }

    // A single failed/empty snapshot doesn't mean the connection itself is broken — don't evict
    // an otherwise-healthy cached Camera just because one snapshot attempt came back empty.
    var image = await mediaService.GetImage();
    return image == null ? new SnapshotActorResult([], null) : new SnapshotActorResult(image.Data, image.MimeType);
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
    // Distinct actor type names already stop a call from crossing nodes; this catches the one case
    // they cannot — two nodes sharing an OWN_SERVER_ID, hence one ring. Without it the camera is
    // simply unreachable from here and the failure is indistinguishable from a dead camera.
    if (!string.IsNullOrEmpty(conn.Domain)
      && !string.Equals(conn.Domain, domain.OwnServerId, StringComparison.OrdinalIgnoreCase))
    {
      throw new InvalidOperationException(
        $"Camera belongs to node '{conn.Domain}' but this actor host serves '{domain.OwnServerId}' — "
        + "two nodes are configured with the same OWN_SERVER_ID");
    }

    var key = $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}:{conn.Xaddr}";
    if (_camera is null || _cameraKey != key)
    {
      _camera = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass, logger: new OnvifLoggerAdapter(Logger), xaddr: conn.Xaddr);
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
