using Dapr.Actors;
using Dapr.Actors.Client;
using Grpc.Core;
using Vms.Onvif;
using web_vms.Types.Actors;

namespace VmsOnvif;

public class PtzServiceImpl(CameraConnCache connCache) : PtzService.PtzServiceBase
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

  private async Task<CameraConnInfo?> GetConn(string sourceId, CancellationToken ct) =>
    (await connCache.GetAsync(sourceId, ct))?.Conn;

  private static ActorProxy CameraActorProxy(string cameraId) =>
    ActorProxy.Create(new ActorId(cameraId), "CameraActor");

  private static PtzResponse Ok() => new() { Success = true };
  private static PtzResponse Fail(string error) => new() { Success = false, Error = error };
}
