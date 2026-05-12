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
  FsService.FsServiceClient _fsClient
  ) : ControllerBase
{
  private static readonly TimeSpan MediaCallTimeout = TimeSpan.FromSeconds(5);

  private CameraControlService.CameraControlServiceClient GetMediaClient(string? serverId)
  {
    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for media server requests");

    return new CameraControlService.CameraControlServiceClient(DaprClient.CreateInvocationInvoker(serverId));
  }

  private static CallOptions MediaCallOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(MediaCallTimeout), cancellationToken: ct);

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
    var cred = request.Cred != null
      ? new Credentials { User = request.Cred.User, Pass = request.Cred.Pass }
      : null;

    var streams = request.Streams;
    if (streams == null || streams.Count == 0)
    {
      // Legacy single-stream path
      return [new CameraState
      {
        CameraId = request.CameraId,
        RtspUrl  = request.RtspUrl ?? "",
        WebRtc   = request.WebRtc,
        Cred     = cred,
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
        CameraId      = request.CameraId,
        StreamName    = stream.StreamName,
        RtspUrl       = stream.RtspUrl ?? "",
        WebRtc        = stream.WebRtc,
        Cred          = cred,
        RecordingMode = recordingMode,
      };

      if (recordingMode == RecordingMode.RecordingPrealarm && profile != null)
        state.SegmentSeconds = profile.SegmentSeconds > 0 ? profile.SegmentSeconds : 5u;

      states.Add(state);
    }
    return states;
  }

  [HttpPost("cameras")]
  public async Task<IActionResult> AddCamera([FromBody] CameraSetupDto request, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(request.ServerId))
    {
      return BadRequest("serverId must not be empty");
    }

    var normalized = CameraManager.Normalize(request);

    // Only notify the media server if there is a stream to actually run.
    // A camera with no streams is valid config — the user will add streams separately.
    var hasStream = normalized.Streams.Count > 0 || !string.IsNullOrEmpty(normalized.RtspUrl);
    if (hasStream)
    {
      var mediaClient = GetMediaClient(normalized.ServerId!);
      foreach (var controlState in ToControlStates(normalized))
      {
        Vms.Control.OperationStatus mediaResponse;
        try
        {
          mediaResponse = await mediaClient.SetCameraStateAsync(controlState, MediaCallOptions(ct));
        }
        catch (RpcException ex)
        {
          return Ok(new ApiResponse(false, MediaServerError(ex)));
        }

        if (!mediaResponse.Success)
          return Ok(ApiResponseMapper.ToApiResponse(mediaResponse));
      }
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
      await _manager.Add(CameraManager.Normalize(request), ct);
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

    var normalized = CameraManager.Normalize(request);
    var addClient = GetMediaClient(normalized.ServerId!);
    foreach (var controlState in ToControlStates(normalized))
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
    return Ok(await _manager.List());
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

    var normalized = CameraManager.Normalize(state with { ServerId = targetServerId });
    var stateClient = GetMediaClient(targetServerId);
    Vms.Control.OperationStatus lastResponse = new() { Success = true, Message = "ok" };
    foreach (var controlState in ToControlStates(normalized))
    {
      try
      {
        lastResponse = await stateClient.SetCameraStateAsync(controlState, MediaCallOptions(ct));
      }
      catch (RpcException ex)
      {
        return Ok(new ApiResponse(false, MediaServerError(ex)));
      }

      if (!lastResponse.Success)
        return Ok(ApiResponseMapper.ToApiResponse(lastResponse));
    }

    await _manager.SetSetup(normalized, ct);
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

    if (string.Equals(normalized.RecordingMode, "off", StringComparison.OrdinalIgnoreCase))
    {
      return Ok(normalized with { ActiveRetentionWindow = false });
    }

    try
    {
      var retention = await _fsClient.GetCameraRetentionStatusAsync(
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
