using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using web_vms.Authorization;

namespace web_vms;

/// <summary>
/// Authenticated WHEP gateway for live video — see <see cref="WhepProxyService"/> for the actual
/// proxying logic. Browsers never address MediaMTX directly.
/// </summary>
[ApiController]
[Route("api/live/whep")]
public class WhepController(
  WhepProxyService _proxy,
  ICameraAccessService _access,
  ICameraManager _cameras,
  ServerPairRegistry _registry) : ControllerBase
{
  // Short: the offer is on the critical path of a viewer waiting for a picture, and the push is
  // started for its side effect — the answer does not depend on it.
  private static readonly TimeSpan EnsureTimeout = TimeSpan.FromSeconds(2);

  // Whose session this is. Taken from the validated token, never from the request, exactly as
  // MonitorController does it. Empty when authentication is disabled — then everyone shares one
  // bucket, the same way monitor ownership already behaves standalone.
  private string Owner => User.Identity?.Name ?? string.Empty;

  // The camera id is the first segment of the MediaMTX path, so the check lives here rather than
  // in the generic filter.
  [CameraAccess(CameraAction.ViewLive, Source = CameraIdSource.Action)]
  [HttpPost("offer/{*path}")]
  public async Task<IActionResult> CreateOffer(string path, CancellationToken ct)
  {
    var slash = path?.IndexOf('/') ?? -1;
    var cameraId = slash < 0 ? path : path![..slash];
    if (string.IsNullOrWhiteSpace(cameraId))
      return BadRequest("camera id is required");

    if (!await _access.IsAllowedAsync(User, cameraId, CameraAction.ViewLive, ct))
      return StatusCode(StatusCodes.Status403Forbidden);

    // A viewer has just arrived, which is the one moment we know for certain that the push is
    // wanted. Without this the push was only ever (re)started by a poll — either the camera-state
    // endpoint or mediamtx_monitor's own 5 s sweep — so a stream that had been stopped as idle
    // stayed black for seconds after somebody opened it, on top of the wait for a keyframe.
    await EnsurePushAsync(cameraId!, slash < 0 ? null : path![(slash + 1)..], ct);

    var result = await _proxy.CreateOfferAsync(path!, Request.Body, Owner, ct);
    return await result.WriteTo(this, "/api/live/whep/session/", ct);
  }

  /// <summary>
  /// Asks the owning media server to publish this camera's stream, if it is not already. Strictly
  /// best-effort: a media server that is slow or down must not stop the offer from being proxied —
  /// the path may well still be published from an earlier viewer.
  /// </summary>
  private async Task EnsurePushAsync(string cameraId, string? streamName, CancellationToken ct)
  {
    try
    {
      var camera = await _cameras.Get(cameraId, ct);
      if (camera is null || string.IsNullOrWhiteSpace(camera.ServerId))
        return;

      var client = _registry.GetMediaClient(camera.ServerId);

      // The WHEP path names the stream; without one, every WebRTC stream of the camera is meant.
      var streams = string.IsNullOrWhiteSpace(streamName)
        ? (camera.Streams ?? []).Where(s => s.WebRtc && s.Disabled != true).Select(s => s.StreamName)
        : [streamName];

      var options = new CallOptions(
        deadline: DateTime.UtcNow.Add(EnsureTimeout), cancellationToken: ct);

      foreach (var name in streams)
      {
        if (string.IsNullOrWhiteSpace(name)) continue;
        await client.EnsureWebRtcStreamingAsync(
          new EnsureWebRtcStreamingRequest { CameraId = cameraId, StreamName = name }, options);
      }
    }
    catch (RpcException) { /* best-effort */ }
    catch (OperationCanceledException) { /* client went away mid-offer */ }
  }

  // Ending a session the caller owns; ownership is checked inside the proxy against the token.
  [NoCameraAccess("closes the caller's own WHEP session, identified by an opaque token")]
  [HttpDelete("session/{token}")]
  public async Task<IActionResult> DeleteSession(string token, CancellationToken ct)
  {
    var result = await _proxy.DeleteSessionAsync(token, Owner, ct);
    return result.ToActionResult(this);
  }
}
