using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.PtzTracking;

namespace VmsPtzTrackingSingleton.Grpc;

/// The operator-facing surface: point a camera at an object, ask what every camera is doing.
/// All of the work belongs to <see cref="TrackingSessionManager"/>.
///
/// Which cameras track at all is not decided here -- that is configuration, reconciled by
/// <see cref="TrackingReconciler"/>.
public sealed class PtzTrackingServiceImpl(TrackingSessionManager manager)
  : PtzTrackingService.PtzTrackingServiceBase
{
  public override async Task<SetTargetResponse> SetTarget(
    SetTargetRequest request, ServerCallContext context)
  {
    var target = request.Target is null
      ? null
      : new TargetKey(request.Target.Source, request.Target.TrackId, request.Target.Kind);

    var error = await manager.SetTargetAsync(request.CameraId, target, context.CancellationToken);

    return error is null
      ? new SetTargetResponse { Success = true }
      : new SetTargetResponse { Success = false, Error = error };
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
        Active = state.Active,
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
