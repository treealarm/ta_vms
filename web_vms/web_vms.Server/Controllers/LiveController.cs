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

  private static IReadOnlyList<CameraState> ToControlStates(CameraSetupDto request)
  {
    var streams = request.Streams;
    if (streams == null || streams.Count == 0)
    {
      // Legacy single-stream path
      return [new CameraState
      {
        CameraId = request.CameraId,
        WebRtc   = request.WebRtc,
      }];
    }

    var states = new List<CameraState>(streams.Count);
    foreach (var stream in streams)
    {
      var profile = stream.OverrideProfile ?? stream.RecordingProfile;
      // An open recording schedule window wins over the profile and over any override.
      var recordingMode = request.ScheduleActive
        ? RecordingMode.RecordingContinuous
        : profile?.Mode switch
          {
            "continuous" => RecordingMode.RecordingContinuous,
            "prealarm"   => RecordingMode.RecordingPrealarm,
            _            => RecordingMode.RecordingOff,
          };

      var state = new CameraState
      {
        CameraId      = request.CameraId,
        StreamName    = stream.StreamName,
        WebRtc        = stream.WebRtc,
        RecordingMode = recordingMode,
      };

      if (recordingMode == RecordingMode.RecordingPrealarm && profile != null)
        state.SegmentSeconds = profile.SegmentSeconds > 0 ? profile.SegmentSeconds : 5u;

      states.Add(state);
    }
    return states;
  }

  private async Task<(CameraSetupDto normalized, MediaMtxServerDto? mtxServer)>
    ResolveWithMediaMtxAsync(CameraSetupDto request, CancellationToken ct)
  {
    var normalized = CameraManager.Normalize(request);

    var hasWebRtcStream = normalized.Streams.Any(s => s.WebRtc) || normalized.WebRtc;
    if (!hasWebRtcStream)
      return (normalized, null);

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

    // Read-only field: the client never sends it, so fill it from the resolved router — otherwise
    // an add/save response would come back without the ICE config its live preview needs.
    if (server is not null)
      normalized = normalized with { EffectiveWebrtcStunUrl = server.StunUrl };

    return (normalized, server);
  }

  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraSetupDto request, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var (normalized, _) = await ResolveWithMediaMtxAsync(request, ct);

    // Config is the source of truth — save first.
    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    // Camera config is delivered to media_server via pub/sub (config_subscriber).
    // No need for a direct SetCameraState call here — config_subscriber handles it.

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

    var (normalized, _) = await ResolveWithMediaMtxAsync(request, ct);

    // Config is the source of truth — save first, regardless of media server availability.
    try
    {
      await _manager.Add(normalized, ct);
    }
    catch (RpcException ex)
    {
      return Ok(new ApiResponse(false, $"Config service error: {MediaServerError(ex)}"));
    }

    // Notify old media server to remove camera (best-effort — it may be offline).
    var oldServerId = current.ServerId;
    if (!string.IsNullOrWhiteSpace(oldServerId) && oldServerId != normalized.ServerId)
    {
      try
      {
        await GetMediaClient(oldServerId).RemoveCameraAsync(
          new SourceRequest { SourceId = cameraId }, BestEffortCallOptions(ct));
      }
      catch (RpcException) { /* best-effort */ }
    }

    // Camera config on the new server is delivered via pub/sub (config_subscriber).

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
    var analyticsCams = await _manager.AnalyticsEnabledCameraIds(ct);

    var enriched = cameras.Select(c => c with
    {
      Streams = WithWhepUrls(c.CameraId, c.Streams),
      HasAnalytics = analyticsCams.Contains(c.CameraId),
    }).ToList();

    return Ok(enriched);
  }

  private static IReadOnlyList<CameraStreamDto> WithWhepUrls(string cameraId, IReadOnlyList<CameraStreamDto> streams) =>
    streams.Select(s => s.WebRtc
      ? s with { WhepUrl = $"/api/live/whep/offer/{cameraId}/{s.StreamName}" }
      : s).ToList();

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

    // ScheduleActive is server-side truth (VmsCfg recomputes it from the wall clock), so take it
    // from the stored camera rather than from the request body — otherwise saving a camera inside
    // an open schedule window would push "off" to the media server.
    var (normalized, _) = await ResolveWithMediaMtxAsync(
      state with { ServerId = targetServerId, ScheduleActive = curState?.ScheduleActive ?? false }, ct);

    // Config is the source of truth — save first.
    await _manager.SetSetup(normalized, ct);

    // Push recording state + WebRTC flag directly to media_server (best-effort fast path).
    // Config (rtsp_url, push URL, credentials) is not sent here — config_subscriber handles
    // that via pub/sub. Only state (recording policy + web_rtc) is set here for immediacy.
    Vms.Control.OperationStatus lastResponse = new() { Success = true, Message = "ok" };
    try
    {
      var stateClient = GetMediaClient(targetServerId);
      foreach (var controlState in ToControlStates(normalized))
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
    normalized = normalized with
    {
      Streams = WithWhepUrls(normalized.CameraId, normalized.Streams),
      // The monitor renders cameras from this per-camera state poll (not the /cameras list), so the
      // bbox-overlay gate flag has to be set here too — preserved through the `with` copies below.
      HasAnalytics = await _manager.HasEnabledAnalyticsWatch(cameraId, ct),
    };

    // Ensure WebRTC push is active for all webrtc-enabled streams (best-effort).
    // Disabled cameras/streams must not be (re)started here — config_subscriber already
    // tore down their media_server pipeline, and polling this endpoint should not undo that.
    var webRtcStreams = normalized.Disabled == true
      ? []
      : normalized.Streams
        .Where(s => s.WebRtc && s.Disabled != true && !string.IsNullOrWhiteSpace(s.StreamName))
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
      return Ok(normalized with { ActiveRetentionWindow = false, AllStreamsRetained = false, RetainedStreams = [] });
    }

    try
    {
      var fsClient = _registry.GetFsClient(normalized.ServerId);
      var retention = await fsClient.GetCameraRetentionStatusAsync(
        new GetCameraRetentionStatusRequest { CameraId = cameraId },
        cancellationToken: ct);
      return Ok(normalized with
      {
        ActiveRetentionWindow = retention.ActiveRetentionWindow,
        AllStreamsRetained = retention.AllStreamsRetained,
        RetainedStreams = retention.RetainedStreams.ToList(),
      });
    }
    catch (RpcException)
    {
      return Ok(normalized with { ActiveRetentionWindow = false, AllStreamsRetained = false, RetainedStreams = [] });
    }
  }
}
