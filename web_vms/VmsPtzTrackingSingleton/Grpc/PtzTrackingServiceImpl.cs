using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.PtzTracking;

namespace VmsPtzTrackingSingleton.Grpc;

/// The operator-facing surface: arm a camera, point it at an object, ask what it is doing.
/// All of the work belongs to <see cref="TrackingSessionManager"/>.
public sealed class PtzTrackingServiceImpl(TrackingSessionManager manager)
  : PtzTrackingService.PtzTrackingServiceBase
{
  public override async Task<SetTrackingResponse> SetTracking(
    SetTrackingRequest request, ServerCallContext context)
  {
    var target = request.Target is null
      ? null
      : new TargetKey(request.Target.Source, request.Target.TrackId, request.Target.Kind);

    var error = await manager.SetTrackingAsync(
      request.CameraId, request.Enabled, target, context.CancellationToken);

    return error is null
      ? new SetTrackingResponse { Success = true }
      : new SetTrackingResponse { Success = false, Error = error };
  }

  public override Task<ListTrackingStatesResponse> ListTrackingStates(
    ListTrackingStatesRequest request, ServerCallContext context)
  {
    var response = new ListTrackingStatesResponse();
    foreach (var state in manager.States())
    {
      var message = new TrackingState
      {
        CameraId = state.CameraId,
        // Every session held here is running; an armed session that has not acquired anything yet
        // is still active, it just has no target.
        Active = true,
        ExplicitTarget = state.ExplicitTarget,
        LastError = state.LastError,
      };
      if (state.Target is not null)
        message.Target = new TrackingTarget
        {
          Source = state.Target.Source,
          TrackId = state.Target.TrackId,
          Kind = state.Target.Kind,
        };
      if (state.TargetSeenAt is not null)
        message.TargetSeenAt = Timestamp.FromDateTime(
          DateTime.SpecifyKind(state.TargetSeenAt.Value, DateTimeKind.Utc));
      response.States.Add(message);
    }
    return Task.FromResult(response);
  }
}
