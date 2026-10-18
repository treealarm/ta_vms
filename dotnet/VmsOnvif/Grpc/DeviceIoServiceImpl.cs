using Grpc.Core;
using Vms.Onvif;

namespace VmsOnvif;

public class DeviceIoServiceImpl(OnvifSettingsService settingsService) : DeviceIoService.DeviceIoServiceBase
{
  public override async Task<GetRelayOutputsResponse> GetRelayOutputs(CameraRequest request, ServerCallContext context)
  {
    var response = new GetRelayOutputsResponse();
    try
    {
      var relays = await settingsService.GetRelayOutputsAsync(request.SourceId, context.CancellationToken);
      response.Relays.AddRange(relays.Select(r => new RelayOutput
      {
        Token     = r.Token,
        Mode      = ParseMode(r.Mode),
        IdleState = ParseIdleState(r.IdleState),
        DelayMs   = r.DelayMs,
      }));
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
      response.Inputs.AddRange(inputs.Select(i => new DigitalInput
      {
        Token        = i.Token,
        IdleState    = ParseIdleState(i.IdleState ?? "closed"),
        HasIdleState = i.IdleState != null,
      }));
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
