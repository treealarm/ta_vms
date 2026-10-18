using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using Vms.Onvif;
using web_srv.Types.Actors;

namespace VmsOnvif;

public class PtzServiceImpl(
  CameraConnCache connCache,
  OnvifSettingsService settingsService,
  string cameraActorType) : PtzService.PtzServiceBase
{

  public override async Task<PtzCapabilitiesResponse> GetCapabilities(PtzCapabilitiesRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return new PtzCapabilitiesResponse();

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      var caps = await actor.InvokeMethodAsync<CameraConnInfo, PtzCapabilitiesDto>(
        "GetPtzCapabilitiesAsync", conn, context.CancellationToken);
      return new PtzCapabilitiesResponse
      {
        AbsoluteMove   = caps.AbsoluteMove,
        RelativeMove   = caps.RelativeMove,
        ContinuousMove = caps.ContinuousMove,
      };
    }
    catch
    {
      return new PtzCapabilitiesResponse();
    }
  }

  public override async Task<PtzStatusResponse> GetStatus(PtzStatusRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return new PtzStatusResponse { Error = "Camera not found or has no ONVIF connection" };

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      var status = await actor.InvokeMethodAsync<PtzStopActorRequest, PtzStatusDto?>(
        "GetPtzStatusAsync",
        new PtzStopActorRequest(conn, request.ProfileToken),
        context.CancellationToken);

      return status is null
        ? new PtzStatusResponse { Error = "The camera does not report its position" }
        : new PtzStatusResponse
        {
          Available = true,
          Pan       = status.Pan,
          Tilt      = status.Tilt,
          Zoom      = status.Zoom,
          Moving    = status.Moving,
        };
    }
    catch (Exception ex)
    {
      return new PtzStatusResponse { Error = ex.Message };
    }
  }

  public override async Task<PtzResponse> ContinuousMove(PtzContinuousMoveRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return Fail("Camera not found or has no ONVIF connection");

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      await actor.InvokeMethodAsync<PtzMoveActorRequest>(
        "PtzContinuousMoveAsync",
        new PtzMoveActorRequest(conn, request.ProfileToken, request.Pan, request.Tilt, request.Zoom),
        context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<PtzResponse> AbsoluteMove(PtzAbsoluteMoveRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return Fail("Camera not found or has no ONVIF connection");

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      await actor.InvokeMethodAsync<PtzAbsoluteMoveActorRequest>(
        "PtzAbsoluteMoveAsync",
        new PtzAbsoluteMoveActorRequest(conn, request.ProfileToken, request.Pan, request.Tilt, request.Zoom, request.Speed),
        context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<PtzResponse> RelativeMove(PtzRelativeMoveRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return Fail("Camera not found or has no ONVIF connection");

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      await actor.InvokeMethodAsync<PtzMoveActorRequest>(
        "PtzRelativeMoveAsync",
        new PtzMoveActorRequest(conn, request.ProfileToken, request.Pan, request.Tilt, request.Zoom),
        context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<PtzResponse> Stop(PtzStopRequest request, ServerCallContext context)
  {
    var conn = await GetConn(request.SourceId, context.CancellationToken);
    if (conn == null)
      return Fail("Camera not found or has no ONVIF connection");

    try
    {
      var actor = CameraActorProxy(request.SourceId);
      await actor.InvokeMethodAsync<PtzStopActorRequest>(
        "PtzStopAsync",
        new PtzStopActorRequest(conn, request.ProfileToken),
        context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<PtzPresetsResponse> GetPresets(PtzPresetsRequest request, ServerCallContext context)
  {
    try
    {
      var presets = await settingsService.GetPresetsAsync(
        request.SourceId, request.ProfileToken, context.CancellationToken);
      var response = new PtzPresetsResponse();
      response.Presets.AddRange(presets.Select(p => new PtzPreset { Token = p.Token, Name = p.Name }));
      return response;
    }
    catch (Exception ex)
    {
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<PtzSetPresetResponse> SetPreset(PtzSetPresetRequest request, ServerCallContext context)
  {
    try
    {
      var token = await settingsService.SetPresetAsync(
        request.SourceId, request.ProfileToken, request.PresetName, request.PresetToken,
        context.CancellationToken);
      return new PtzSetPresetResponse { Success = true, PresetToken = token };
    }
    catch (Exception ex)
    {
      return new PtzSetPresetResponse { Success = false, Error = ex.Message };
    }
  }

  public override async Task<PtzResponse> GotoPreset(PtzGotoPresetRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.GotoPresetAsync(
        request.SourceId, request.ProfileToken, request.PresetToken, context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<PtzResponse> RemovePreset(PtzRemovePresetRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.RemovePresetAsync(
        request.SourceId, request.ProfileToken, request.PresetToken, context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  private async Task<CameraConnInfo?> GetConn(string sourceId, CancellationToken ct) =>
    (await connCache.GetAsync(sourceId, ct))?.Conn;

  // The type name carries this node — see CameraActorType.
  private ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), cameraActorType);

  private static PtzResponse Ok() => new() { Success = true };
  private static PtzResponse Fail(string error) => new() { Success = false, Error = error };
}
