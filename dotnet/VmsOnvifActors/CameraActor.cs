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

  public Task<PtzCommandActorResult> PtzContinuousMoveAsync(PtzMoveActorRequest request) =>
    CommandAsync(request.Conn, ptz =>
      ptz.ContinuousMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom));

  public Task<PtzCommandActorResult> PtzAbsoluteMoveAsync(PtzAbsoluteMoveActorRequest request) =>
    CommandAsync(request.Conn, ptz =>
    {
      var speed = request.Speed > 0 ? request.Speed : 0.5f;
      return ptz.AbsoluteMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom, speed, speed);
    });

  public Task<PtzCommandActorResult> PtzRelativeMoveAsync(PtzMoveActorRequest request) =>
    CommandAsync(request.Conn, ptz =>
    {
      var speed = 0.5f;
      return ptz.RelativeMoveAsync(request.ProfileToken, request.Pan, request.Tilt, request.Zoom, speed, speed);
    });

  public Task<PtzCommandActorResult> PtzStopAsync(PtzStopActorRequest request) =>
    CommandAsync(request.Conn, ptz => ptz.StopAsync(request.ProfileToken));

  public async Task<PtzCapabilitiesDto> GetPtzCapabilitiesAsync(CameraConnInfo conn)
  {
    var ptz = await FindPtzAsync(conn, CameraKey(conn));
    if (ptz is null)
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
    var ptz = await FindPtzAsync(request.Conn, key);
    if (ptz is null)
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

  /// <summary>Runs one PTZ command and reports its outcome as a value.</summary>
  /// <remarks>
  /// Both halves of an outage end here as a failed result. Discovery failing is the cold half;
  /// the command itself throwing is the warm one — the camera stopped answering after its services
  /// were resolved, and OnvifLib's move calls, unlike its status call, do not swallow that.
  /// </remarks>
  private async Task<PtzCommandActorResult> CommandAsync(
    CameraConnInfo conn, Func<PtzService2, Task> command)
  {
    var key = CameraKey(conn);
    var ptz = await FindPtzAsync(conn, key);
    if (ptz == null)
      return new PtzCommandActorResult(false, $"PTZ service not available for {conn.Ip}:{conn.Port}");

    try
    {
      await command(ptz);
      return new PtzCommandActorResult(true);
    }
    catch (Exception ex)
    {
      // Without this note every command would go to the dead camera again; on a camera that drops
      // packets rather than refusing them, each attempt also holds the actor for the ONVIF timeout.
      NoteServiceFailure(key);
      return new PtzCommandActorResult(false, $"{ex.GetType().Name}: {ex.Message}");
    }
  }

  /// <returns>Null while backed off, when the camera does not answer, or when it has no PTZ.</returns>
  private async Task<PtzService2?> FindPtzAsync(CameraConnInfo conn, string key)
  {
    if (_backoff.IsBackedOff(key))
      return null;

    var camera = EnsureCamera(conn, key);
    if (await camera.GetServicesAsync() is null)
    {
      NoteServiceFailure(key);
      return null;
    }

    return await camera.GetPtzService();
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
