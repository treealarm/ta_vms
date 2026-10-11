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

  public override async Task<AimAtMasterPointResponse> AimAtMasterPoint(
    AimAtMasterPointRequest request, ServerCallContext context)
  {
    var result = await manager.AimAtMasterPointAsync(
      request.SlaveCameraId, request.MasterX, request.MasterY, context.CancellationToken);

    if (!result.Success)
      return new AimAtMasterPointResponse { Success = false, Error = result.Error ?? string.Empty };

    var response = new AimAtMasterPointResponse
    {
      Success      = true,
      PresetToken  = result.PresetToken ?? string.Empty,
      PresetName   = result.PresetName ?? string.Empty,
      Extrapolated = result.Extrapolated,
    };
    if (result.Pan is not null) response.Pan = result.Pan.Value;
    if (result.Tilt is not null) response.Tilt = result.Tilt.Value;
    return response;
  }

  public override async Task<HoldForSurveyResponse> HoldForSurvey(
    HoldForSurveyRequest request, ServerCallContext context)
  {
    var error = await manager.HoldAsync(
      request.SlaveCameraId, TimeSpan.FromSeconds(request.Seconds));

    return error is null
      ? new HoldForSurveyResponse { Success = true }
      : new HoldForSurveyResponse { Success = false, Error = error };
  }

  public override async Task<GetSlaveBindingFitResponse> GetSlaveBindingFit(
    GetSlaveBindingFitRequest request, ServerCallContext context)
  {
    var binding = await manager.GetBindingFitAsync(request.SlaveCameraId, context.CancellationToken);
    if (binding is null)
      return new GetSlaveBindingFitResponse();

    var response = new GetSlaveBindingFitResponse
    {
      Found    = true,
      Fitted   = binding.Fit is not null,
      Error    = binding.FitError ?? string.Empty,
      Unusable = binding.Unusable() ?? string.Empty,
    };

    // Indexed against the binding's own points rather than the fit's: the fit drops points with no
    // readable position, so its indices would not line up with the list the operator sees. It drops
    // them in order and keeps the rest, so one forward cursor pairs the two lists back up — which
    // searching by value could not do, the points being records that compare equal by content.
    var fitted = binding.Fit;
    var cursor = 0;
    foreach (var (point, ordinal) in binding.Points.Select((p, i) => (p, i)))
    {
      var residual = new PtzSlaveBindingResidual { Ordinal = ordinal };
      if (fitted is not null && cursor < fitted.Points.Count && ReferenceEquals(fitted.Points[cursor], point))
      {
        if (fitted.Residuals[cursor] is { } value)
          residual.Residual = value;
        cursor++;
      }
      response.Residuals.Add(residual);
    }
    return response;
  }

  public override async Task<AimAtWorldPointResponse> AimAtWorldPoint(
    AimAtWorldPointRequest request, ServerCallContext context)
  {
    var result = await manager.AimAtWorldPointAsync(
      request.CameraId, request.SpaceId, request.Lat, request.Lon, request.HeightM, context.CancellationToken);

    if (!result.Success)
      return new AimAtWorldPointResponse { Success = false, Error = result.Error ?? string.Empty };

    return new AimAtWorldPointResponse
    {
      Success      = true,
      Pan          = result.Aim!.Pan,
      Tilt         = result.Aim.Tilt,
      Extrapolated = result.Aim.Extrapolated,
      AzimuthDeg   = result.Bearing!.Value.AzimuthDeg,
      ElevationDeg = result.Bearing.Value.ElevationDeg,
      DistanceM    = result.Bearing.Value.DistanceM,
    };
  }

  public override async Task<GetPtzGeoFitResponse> GetPtzGeoFit(
    GetPtzGeoFitRequest request, ServerCallContext context)
  {
    var model = await manager.GetSpatialModelAsync(request.CameraId, context.CancellationToken);
    if (model is null)
      return new GetPtzGeoFitResponse { Found = false };

    var response = new GetPtzGeoFitResponse
    {
      Found  = true,
      Fitted = model.GeoFit is not null,
      Error  = model.GeoFitError ?? string.Empty,
    };
    foreach (var observation in model.Observations)
    {
      var residual = new PtzGeoResidual { LandmarkId = observation.LandmarkId };
      if (model.GeoFit?.Residuals.GetValueOrDefault(observation.LandmarkId) is { } value)
      {
        residual.Pan = value.Pan;
        residual.Tilt = value.Tilt;
      }
      response.Residuals.Add(residual);
    }
    return response;
  }

  public override async Task<GetGroundFitResponse> GetGroundFit(
    GetGroundFitRequest request, ServerCallContext context)
  {
    var model = await manager.GetSpatialModelAsync(request.CameraId, context.CancellationToken);
    if (model is null)
      return new GetGroundFitResponse { Found = false };

    var response = new GetGroundFitResponse
    {
      Found  = true,
      Fitted = model.GroundFit is not null,
      Error  = model.GroundFitError ?? string.Empty,
    };
    foreach (var sample in model.GroundSamples)
    {
      var residual = new GroundResidual { LandmarkId = sample.LandmarkId };
      if (model.GroundFit?.Residuals.GetValueOrDefault(sample.LandmarkId) is { } metres)
        residual.DistanceM = metres;
      response.Residuals.Add(residual);
    }
    if (model.GroundFit is { } fit)
      response.Matrix.AddRange(fit.ToWgs84(model.Frame));
    return response;
  }

  public override async Task<ListGroundProjectionsResponse> ListGroundProjections(
    ListGroundProjectionsRequest request, ServerCallContext context)
  {
    var response = new ListGroundProjectionsResponse();
    foreach (var model in await manager.ListGroundProjectionsAsync(request.SpaceId, context.CancellationToken))
    {
      var projection = new GroundProjection { CameraId = model.CameraId };
      projection.Matrix.AddRange(model.GroundFit!.ToWgs84(model.Frame));
      response.Projections.Add(projection);
    }
    return response;
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
      if (state.MasterCameraId is not null)
        message.MasterCameraId = state.MasterCameraId;
      if (state.TargetSeenAt is not null)
        message.TargetSeenAt = Timestamp.FromDateTime(
          DateTime.SpecifyKind(state.TargetSeenAt.Value, DateTimeKind.Utc));
      response.States.Add(message);
    }
    return Task.FromResult(response);
  }
}
