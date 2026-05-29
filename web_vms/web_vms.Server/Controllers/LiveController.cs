using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using Vms.Fs;
using web_vms.Types.Dto;
using RecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

[ApiController]
[Route("api/[controller]")]
public class LiveController(
  ICameraManager _manager,
  ServerPairRegistry _registry
  ) : ControllerBase
{
  private static readonly TimeSpan MediaCallTimeout    = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan MediaCallBestEffort = TimeSpan.FromSeconds(1);

  private CameraControlService.CameraControlServiceClient GetMediaClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for media server requests");

    return new CameraControlService.CameraControlServiceClient(DaprClient.CreateInvocationInvoker(serverId));
  }

  private static CallOptions MediaCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

  private static CallOptions BestEffortCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallBestEffort), cancellationToken: ct);

  private static string MediaServerError(RpcException ex) => ex.StatusCode switch
  {
    Grpc.Core.StatusCode.DeadlineExceeded => "Media server did not respond within timeout (is it running?)",
    Grpc.Core.StatusCode.Unavailable      => "Media server is unavailable",
    _                                     => string.IsNullOrWhiteSpace(ex.Status.Detail)
                                              ? $"Media server error: {ex.StatusCode}"
                                              : $"Media server error: {ex.Status.Detail}"
  };

  private static IReadOnlyList<CameraState> ToControlStates(CameraSetupDto request, string? webrtcRtspPushUrl = null)
  {
    var cred = request.Cred != null
      ? new Credentials { User = request.Cred.User, Pass = request.Cred.Pass }
      : null;

    var streams = request.Streams;
    if (streams == null || streams.Count == 0)
    {
      // Legacy single-stream path
      return [new CameraState
      {
        CameraId            = request.CameraId,
        RtspUrl             = request.RtspUrl ?? "",
        WebRtc              = request.WebRtc,
        Cred                = cred,
        WebrtcRtspPushUrl   = webrtcRtspPushUrl ?? "",
      }];
    }

    var states = new List<CameraState>(streams.Count);
    foreach (var stream in streams)
    {
      var profile = stream.OverrideProfile ?? stream.RecordingProfile;
      var recordingMode = profile?.Mode switch
      {
        "continuous" => RecordingMode.RecordingContinuous,
        "prealarm"   => RecordingMode.RecordingPrealarm,
        _            => RecordingMode.RecordingOff,
      };

      var state = new CameraState
      {
        CameraId            = request.CameraId,
        StreamName          = stream.StreamName,
        RtspUrl             = stream.RtspUrl ?? "",
        ProfileToken        = stream.ProfileToken ?? "",
        WebRtc              = stream.WebRtc,
        Cred                = cred,
        RecordingMode       = recordingMode,
        WebrtcRtspPushUrl   = webrtcRtspPushUrl ?? "",
      };

      if (recordingMode == RecordingMode.RecordingPrealarm && profile != null)
        state.SegmentSeconds = profile.SegmentSeconds > 0 ? profile.SegmentSeconds : 5u;

      states.Add(state);
    }
    return states;
  }

  private async Task<(CameraSetupDto normalized, string? webrtcRtspPushUrl, string? webrtcUrl)>
    ResolveWithMediaMtxAsync(CameraSetupDto request, CancellationToken ct)
  {
    var normalized = CameraManager.Normalize(request);

    var hasWebRtcStream = normalized.Streams.Any(s => s.WebRtc) || normalized.WebRtc;
    if (!hasWebRtcStream)
      return (normalized, null, null);

    MediaMtxServerDto? server;
    if (!string.IsNullOrWhiteSpace(normalized.MediaMtxServerId))
    {
      // User explicitly chose a server — respect that choice, don't overwrite with DB value
      var servers = await _manager.ListMediaMtxServers(ct);
      server = servers.FirstOrDefault(s => s.Id == normalized.MediaMtxServerId);
    }
    else
    {
      // No server specified — auto-assign and patch back the assigned ID
      server = await _manager.GetOrAssignMediaMtxServer(normalized.CameraId, ct);
      if (server is not null && normalized.MediaMtxServerId != server.Id)
        normalized = normalized with { MediaMtxServerId = server.Id };
    }

    if (server is null)
      return (normalized, null, null);

    return (normalized, server.RtspUrl, server.WebrtcUrl);
  }

  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraSetupDto request, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var (normalized, webrtcRtspPushUrl, _) = await ResolveWithMediaMtxAsync(request, ct);

    // Config is the source of truth — save first.
    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    // Notify media server best-effort (don't fail the save if media server is unavailable).
    var hasStream = normalized.Streams.Count > 0 || !string.IsNullOrEmpty(normalized.RtspUrl);
    if (hasStream && !string.IsNullOrWhiteSpace(normalized.ServerId))
    {
      try
      {
        var mediaClient = GetMediaClient(normalized.ServerId);
        foreach (var controlState in ToControlStates(normalized, webrtcRtspPushUrl))
          await mediaClient.SetCameraStateAsync(controlState, BestEffortCallOptions(ct));
      }
      catch (RpcException) { /* best-effort */ }
    }

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpPut("cameras/{cameraId}/parent")]
  public async Task<IActionResult> ChangeCameraParent(
    string cameraId,
    [FromBody] CameraSetupDto request,
    CancellationToken ct)
  {
    if (cameraId != request.CameraId)
    {
      return BadRequest("cameraId in route and body must match");
    }

    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var current = await _manager.Get(cameraId);
    if (current is null)
    {
      return NotFound();
    }

    if (current.ServerId == request.ServerId)
    {
      var (sameParentNorm, _, _) = await ResolveWithMediaMtxAsync(request, ct);
      await _manager.Add(sameParentNorm, ct);
      return Ok(new ApiResponse(true, "Camera parent is unchanged"));
    }

    Vms.Control.OperationStatus removeResponse;
    try
    {
      removeResponse = await GetMediaClient(current.ServerId).RemoveCameraAsync(
        new SourceRequest { SourceId = cameraId }, MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (!removeResponse.Success)
      return Ok(ApiResponseMapper.ToApiResponse(removeResponse));

    var (normalized, webrtcRtspPushUrl, _) = await ResolveWithMediaMtxAsync(request, ct);
    var addClient = GetMediaClient(normalized.ServerId!);
    foreach (var controlState in ToControlStates(normalized, webrtcRtspPushUrl))
    {
      Vms.Control.OperationStatus addResponse;
      try
      {
        addResponse = await addClient.SetCameraStateAsync(controlState, MediaCallOptions(ct));
      }
      catch (RpcException ex)
      {
        return Ok(new ApiResponse(false, MediaServerError(ex)));
      }

      if (!addResponse.Success)
        return Ok(ApiResponseMapper.ToApiResponse(addResponse));
    }

    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpDelete("cameras/{cameraId}")]
  public async Task<IActionResult> RemoveCamera(string cameraId, CancellationToken ct)
  {
    var current = await _manager.Get(cameraId);
    if (string.IsNullOrWhiteSpace(current?.ServerId))
    {
      return NotFound();
    }

    Vms.Control.OperationStatus response;
    try
    {
      response = await GetMediaClient(current.ServerId).RemoveCameraAsync(
        new SourceRequest { SourceId = cameraId }, MediaCallOptions(ct));
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, MediaServerError(ex)));
    }

    if (response.Success)
      await _manager.Remove(cameraId, ct);

    return Ok(ApiResponseMapper.ToApiResponse(response));
  }

  [HttpGet("servers")]
  public async Task<IActionResult> ListServers(CancellationToken ct)
  {
    return Ok(await _manager.ListServers());
  }

  [HttpGet("cameras")]
  public async Task<IActionResult> ListCameras(CancellationToken ct)
  {
    var cameras = await _manager.List(ct);

    // Enrich each camera with its assigned MediaMTX WebRTC URL (best-effort; no auto-assign on list)
    var allMtxServers = (await _manager.ListMediaMtxServers(ct))
      .ToDictionary(s => s.Id, s => s.WebrtcUrl);

    var enriched = cameras.Select(c =>
    {
      if (!string.IsNullOrEmpty(c.MediaMtxServerId) && allMtxServers.TryGetValue(c.MediaMtxServerId, out var url))
        return c with { MediaMtxWebrtcUrl = url };
      return c;
    }).ToList();

    return Ok(enriched);
  }

  [HttpPost("cameras/state")]
  public async Task<IActionResult> SetCameraState([FromBody] CameraSetupDto state, CancellationToken ct)
  {
    var curState = await _manager.Get(state.CameraId);
    if (curState != null && state.OnvifConn == null)
    {
      state.OnvifConn = curState.OnvifConn;
    }

    var targetServerId = state.ServerId ?? curState?.ServerId;
    if (string.IsNullOrWhiteSpace(targetServerId))
    {
      return NotFound();
    }

    var (normalized, webrtcRtspPushUrl, _) = await ResolveWithMediaMtxAsync(
      state with { ServerId = targetServerId }, ct);

    // Config is the source of truth — save first.
    await _manager.SetSetup(normalized, ct);

    // Notify media server best-effort.
    Vms.Control.OperationStatus lastResponse = new() { Success = true, Message = "ok" };
    try
    {
      var stateClient = GetMediaClient(targetServerId);
      foreach (var controlState in ToControlStates(normalized, webrtcRtspPushUrl))
      {
        lastResponse = await stateClient.SetCameraStateAsync(controlState, BestEffortCallOptions(ct));
        if (!lastResponse.Success) break;
      }
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(true, $"Saved, but media server error: {MediaServerError(ex)}"));
    }

    return Ok(ApiResponseMapper.ToApiResponse(lastResponse));
  }

  [HttpGet("cameras/{cameraId}/state")]
  public async Task<IActionResult> GetCameraState(string cameraId, CancellationToken ct)
  {
    var camera = await _manager.Get(cameraId);
    if (camera is null)
    {
      return NotFound();
    }

    var normalized = CameraManager.Normalize(camera);

    // Enrich with MediaMTX WebRTC URL for the frontend
    var mediaMtxServer = await _manager.GetOrAssignMediaMtxServer(cameraId, ct);
    normalized = normalized with { MediaMtxWebrtcUrl = mediaMtxServer?.WebrtcUrl };

    // Ensure WebRTC push is active for all webrtc-enabled streams (best-effort)
    var webRtcStreams = normalized.Streams
      .Where(s => s.WebRtc && !string.IsNullOrWhiteSpace(s.StreamName))
      .ToList();

    if (webRtcStreams.Count > 0 && !string.IsNullOrWhiteSpace(normalized.ServerId))
    {
      try
      {
        var mediaClient = GetMediaClient(normalized.ServerId);
        foreach (var stream in webRtcStreams)
        {
          await mediaClient.EnsureWebRtcStreamingAsync(
            new EnsureWebRtcStreamingRequest { CameraId = cameraId, StreamName = stream.StreamName },
            BestEffortCallOptions(ct));
        }
      }
      catch (RpcException) { /* best-effort: don't fail state query if media server is unavailable */ }
    }

    if (string.Equals(normalized.RecordingMode, "off", StringComparison.OrdinalIgnoreCase))
    {
      return Ok(normalized with { ActiveRetentionWindow = false });
    }

    try
    {
      var fsClient = _registry.GetFsClient(normalized.ServerId);
      var retention = await fsClient.GetCameraRetentionStatusAsync(
        new GetCameraRetentionStatusRequest { CameraId = cameraId },
        cancellationToken: ct);
      return Ok(normalized with { ActiveRetentionWindow = retention.ActiveRetentionWindow });
    }
    catch (RpcException)
    {
      return Ok(normalized with { ActiveRetentionWindow = false });
    }
  }
}
