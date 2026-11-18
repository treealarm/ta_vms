using Dapr.Actors.Runtime;
using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvifActors;

/// The node this actor host serves. Injected so the actor can reject a camera from another node
/// rather than time out trying to reach it.
public sealed record CameraActorDomain(string OwnServerId);

public class CameraActor(ActorHost host, CameraActorDomain domain, IOnvifLogger onvifLogger, TimeProvider time) : Actor(host), ICameraActor
{
  private static readonly TimeSpan ServiceFailureWindow = TimeSpan.FromSeconds(5);

  private Camera? _camera;
  private string _cameraKey = string.Empty;
  private readonly CameraUnavailableBackoff _backoff = new(ServiceFailureWindow, time);

  public async Task<List<ProfileInfoDto>> GetProfilesAsync(CameraConnInfo conn)
  {
    var key = CameraKey(conn);
    if (_backoff.IsBackedOff(key))
      return [];

    var camera = EnsureCamera(conn, key);
    var mediaService = await camera.GetMediaService();

    if (mediaService == null)
    {
      NoteServiceFailure(key);
      return [];
    }

    return mediaService.GetProfiles()
      .Select(p => new ProfileInfoDto(p.Token, p.Name, p.Width, p.Height, p.Encoding, p.VideoSourceToken))
      .ToList();
  }

  public async Task<GetStreamUriActorResult> GetStreamUriAsync(GetStreamUriActorRequest request)
  {
    var key = CameraKey(request.Conn);
    if (_backoff.IsBackedOff(key))
      return new GetStreamUriActorResult(string.Empty);

    var camera = EnsureCamera(request.Conn, key);
    var mediaService = await camera.GetMediaService();

    if (mediaService == null)
    {
      NoteServiceFailure(key);
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
    var key = CameraKey(conn);
    if (_backoff.IsBackedOff(key))
      return new PtzCapabilitiesDto(false, false, false);

    var camera = EnsureCamera(conn, key);
    if (await camera.GetServicesAsync() is null)
    {
      NoteServiceFailure(key);
      return new PtzCapabilitiesDto(false, false, false);
    }

    var ptz = await camera.GetPtzService();
    if (ptz == null)
      return new PtzCapabilitiesDto(false, false, false);

    var caps = await ptz.GetCapabilitiesAsync(string.Empty);
    return new PtzCapabilitiesDto(caps.AbsoluteMove, caps.RelativeMove, caps.ContinuousMove);
  }

  /// <summary>Where the camera says it is pointing.</summary>
  /// <remarks>
  /// Shares <see cref="PtzStopActorRequest"/> rather than growing a record of its own: both are a
  /// connection and a profile token, and a second identical shape would be two things to keep in
  /// step for no gain.
  /// </remarks>
  public async Task<PtzStatusDto?> GetPtzStatusAsync(PtzStopActorRequest request)
  {
    var key = CameraKey(request.Conn);
    if (_backoff.IsBackedOff(key))
      return null;

    var camera = EnsureCamera(request.Conn, key);
    if (await camera.GetServicesAsync() is null)
    {
      NoteServiceFailure(key);
      return null;
    }

    var ptz = await camera.GetPtzService();
    if (ptz == null)
      return null;

    var status = await ptz.GetStatusAsync(request.ProfileToken);
    if (status == null)
    {
      // The services resolved before, so the camera stopped answering after that: without the
      // window every poll would pay for the same failing call again.
      NoteServiceFailure(key);
      return null;
    }

    return new PtzStatusDto(status.Pan, status.Tilt, status.Zoom, status.Moving);
  }

  public async Task<SnapshotActorResult> GetSnapshotAsync(CameraConnInfo conn)
  {
    var key = CameraKey(conn);
    if (_backoff.IsBackedOff(key))
      return new SnapshotActorResult([], null);

    var camera = EnsureCamera(conn, key);
    var mediaService = await camera.GetMediaService();
    if (mediaService == null)
    {
      // Service-resolution failure (auth/connectivity) — the cached Camera is no good, discard it.
      NoteServiceFailure(key);
      return new SnapshotActorResult([], null);
    }

    // A single failed/empty snapshot doesn't mean the connection itself is broken — don't evict
    // an otherwise-healthy cached Camera just because one snapshot attempt came back empty.
    var image = await mediaService.GetImage();
    return image == null ? new SnapshotActorResult([], null) : new SnapshotActorResult(image.Data, image.MimeType);
  }

  private async Task<PtzService2> GetPtzOrThrow(CameraConnInfo conn)
  {
    var key = CameraKey(conn);
    if (_backoff.IsBackedOff(key))
      throw new InvalidOperationException($"PTZ service not available for {conn.Ip}:{conn.Port}");

    var camera = EnsureCamera(conn, key);
    if (await camera.GetServicesAsync() is null)
    {
      NoteServiceFailure(key);
      throw new InvalidOperationException($"PTZ service not available for {conn.Ip}:{conn.Port}");
    }

    var ptz = await camera.GetPtzService();
    if (ptz == null)
      throw new InvalidOperationException($"PTZ service not available for {conn.Ip}:{conn.Port}");

    return ptz;
  }

  protected override Task OnDeactivateAsync()
  {
    Evict();
    return base.OnDeactivateAsync();
  }

  private Camera EnsureCamera(CameraConnInfo conn, string key)
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

    if (_camera is null || _cameraKey != key)
    {
      _camera = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass, logger: onvifLogger, xaddr: conn.Xaddr);
      _cameraKey = key;
    }
    return _camera;
  }

  /// <summary>A connection's identity, in the form the cache and the window compare.</summary>
  /// <remarks>
  /// Holds the password, so it is never logged: the actor's own messages use the address only.
  /// </remarks>
  private static string CameraKey(CameraConnInfo conn) =>
    $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}:{conn.Xaddr}";

  /// <summary>
  /// The camera did not answer: throw the cached connection away and stop asking again for the
  /// short window the backoff keeps. One discovery per window is what keeps a switched-off camera
  /// from writing a line per second.
  /// </summary>
  private void NoteServiceFailure(string key)
  {
    _backoff.Note(key);
    Evict();
  }

  private void Evict()
  {
    _camera = null;
    _cameraKey = string.Empty;
  }
}
