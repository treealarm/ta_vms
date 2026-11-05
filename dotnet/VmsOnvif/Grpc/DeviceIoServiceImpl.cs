using Grpc.Core;
using OnvifLib;
using Vms.Onvif;

namespace VmsOnvif;

public class DeviceIoServiceImpl(
  OnvifSettingsService settingsService,
  DeviceIoStateService stateService) : DeviceIoService.DeviceIoServiceBase
{
  public override async Task<GetRelayOutputsResponse> GetRelayOutputs(CameraRequest request, ServerCallContext context)
  {
    var response = new GetRelayOutputsResponse();
    try
    {
      var relays = await settingsService.GetRelayOutputsAsync(request.SourceId, context.CancellationToken);
      response.Relays.AddRange(relays.Select(r =>
      {
        var relay = new RelayOutput
        {
          Token     = r.Token,
          Mode      = ParseMode(r.Mode),
          IdleState = ParseIdleState(r.IdleState),
          DelayMs   = r.DelayMs,
        };
        if (stateService.TryGetLogicalState(request.SourceId, DeviceIoKind.Relay, r.Token) is { } active)
          relay.LiveActive = active;
        return relay;
      }));

      // Opening the subscription is a network round trip of its own; the live values reach the
      // caller's next poll. Its first report also starts or stops the watcher as I/O appears or
      // goes away.
      _ = stateService.ReportDevicePresenceAsync(
        request.SourceId, DeviceIoKind.Relay, relays.Count);
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override async Task<GetRelayOutputOptionsResponse> GetRelayOutputOptions(CameraRequest request, ServerCallContext context)
  {
    var response = new GetRelayOutputOptionsResponse();
    try
    {
      var options = await settingsService.GetRelayOutputOptionsAsync(request.SourceId, context.CancellationToken);
      response.Options.AddRange(options.Select(o =>
      {
        var opt = new RelayOutputOptions
        {
          Token       = o.Token,
          Discrete    = o.Discrete,
          MinDelayMs  = o.MinDelayMs,
          MaxDelayMs  = o.MaxDelayMs,
        };
        opt.SupportedModes.AddRange(o.SupportedModes.Select(ParseMode));
        if (o.DiscreteDelaysMs != null) opt.DiscreteDelaysMs.AddRange(o.DiscreteDelaysMs);
        return opt;
      }));
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override async Task<DeviceIoResponse> SetRelayOutputState(SetRelayOutputStateRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.SetRelayOutputStateAsync(request.SourceId, request.Token, request.Active, context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<DeviceIoResponse> SetRelayOutputSettings(SetRelayOutputSettingsRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.SetRelayOutputSettingsAsync(
        request.SourceId, request.Token, FormatMode(request.Mode), FormatIdleState(request.IdleState), request.DelayMs,
        context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<GetDigitalInputsResponse> GetDigitalInputs(CameraRequest request, ServerCallContext context)
  {
    var response = new GetDigitalInputsResponse();
    try
    {
      var inputs = await settingsService.GetDigitalInputsAsync(request.SourceId, context.CancellationToken);
      response.Inputs.AddRange(inputs.Select(i =>
      {
        var input = new DigitalInput
        {
          Token        = i.Token,
          IdleState    = ParseIdleState(i.IdleState ?? "closed"),
          HasIdleState = i.IdleState != null,
        };
        if (stateService.TryGetLogicalState(request.SourceId, DeviceIoKind.DigitalInput, i.Token) is { } active)
          input.LiveActive = active;
        return input;
      }));

      _ = stateService.ReportDevicePresenceAsync(
        request.SourceId, DeviceIoKind.DigitalInput, inputs.Count);
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override async Task<GetDigitalInputOptionsResponse> GetDigitalInputOptions(CameraRequest request, ServerCallContext context)
  {
    var response = new GetDigitalInputOptionsResponse();
    try
    {
      var options = await settingsService.GetDigitalInputOptionsAsync(request.SourceId, context.CancellationToken);
      response.Options.AddRange(options.Select(o => new DigitalInputOptions
      {
        Token                 = o.Token,
        IdleStateConfigurable = o.IdleStateConfigurable,
      }));
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override async Task<DeviceIoResponse> SetDigitalInputIdleState(SetDigitalInputIdleStateRequest request, ServerCallContext context)
  {
    try
    {
      await settingsService.SetDigitalInputIdleStateAsync(
        request.SourceId, request.Token, FormatIdleState(request.IdleState), context.CancellationToken);
      return Ok();
    }
    catch (Exception ex)
    {
      return Fail(ex.Message);
    }
  }

  public override async Task<GetCameraGeoLocationResponse> GetCameraGeoLocation(CameraRequest request, ServerCallContext context)
  {
    var response = new GetCameraGeoLocationResponse();
    try
    {
      var entry = await settingsService.GetGeoLocationAsync(request.SourceId, context.CancellationToken);
      if (entry?.Location is { } location)
      {
        response.Found = true;
        // Each coordinate is reported only when the camera sent it: an absent latitude is not the
        // equator, and an absent elevation is not sea level.
        if (location.Latitude is { } lat) response.Lat = lat;
        if (location.Longitude is { } lon) response.Lon = lon;
        if (location.ElevationMeters is { } elevation) response.ElevationM = elevation;
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  private static RelayMode ParseMode(string mode) =>
    mode.Equals("bistable", StringComparison.OrdinalIgnoreCase) ? RelayMode.Bistable : RelayMode.Monostable;

  private static string FormatMode(RelayMode mode) =>
    mode == RelayMode.Bistable ? "bistable" : "monostable";

  private static IdleState ParseIdleState(string idleState) =>
    idleState.Equals("open", StringComparison.OrdinalIgnoreCase) ? IdleState.Open : IdleState.Closed;

  private static string FormatIdleState(IdleState idleState) =>
    idleState == IdleState.Open ? "open" : "closed";

  private static DeviceIoResponse Ok() => new() { Success = true };
  private static DeviceIoResponse Fail(string error) => new() { Success = false, Error = error };
}
