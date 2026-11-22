using System.Globalization;
using System.Threading.Channels;
using Grpc.Core;
using OnvifLib;
using Vms.Onvif;

namespace VmsOnvif;

public class DeviceIoServiceImpl(
  OnvifSettingsService settingsService,
  DeviceIoStateService stateService,
  DeviceIoCatalog catalog) : DeviceIoService.DeviceIoServiceBase
{
  public override async Task<GetRelayOutputsResponse> GetRelayOutputs(CameraRequest request, ServerCallContext context)
  {
    var response = new GetRelayOutputsResponse();
    try
    {
      // The list is configuration, and it is served from the presence cache the discovery sweep
      // keeps: opening a wall of tiles must not read the camera once per tile. Only a camera whose
      // list has never been read is asked, and that one answer fills the cache.
      var relays = await catalog.RelaysAsync(request.SourceId, context.CancellationToken);
      response.Relays.AddRange(relays.Select(r =>
      {
        var relay = new RelayOutput
        {
          Token     = r.Token,
          Mode      = ParseMode(r.Mode),
          IdleState = ParseIdleState(r.IdleState),
          DelayMs   = r.DelayMs,
        };
        if (stateService.TryGetSnapshot(request.SourceId, DeviceIoKind.Relay, r.Token) is { } state)
        {
          relay.LiveActive = state.LogicalState;
          relay.Pending = state.Pending;
        }
        return relay;
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
      // The relay's configuration says how long a wish for it lives: a monostable relay drops on
      // the camera's own clock, so a wish for one outlives that drop. This path reads the camera
      // rather than trusting the cache — a command must not be refused, or given the wrong
      // lifetime, because the cache is behind — and that read also opens the device subscription
      // the camera's confirmation arrives on: a command sent before the subscription exists would
      // be applied by the camera and never heard by anyone.
      var relays = await catalog.FreshRelaysAsync(request.SourceId, context.CancellationToken);

      var relay = relays.FirstOrDefault(
        r => string.Equals(r.Token, request.Token, StringComparison.Ordinal));
      if (string.IsNullOrEmpty(relay.Token))
        return Fail($"Camera '{request.SourceId}' has no relay output '{request.Token}'");

      var lifetime = relay is { DelayMs: > 0 }
          && string.Equals(relay.Mode, "monostable", StringComparison.OrdinalIgnoreCase)
        ? TimeSpan.FromMilliseconds(relay.DelayMs) + DeviceIoStateService.MonostableGrace
        : DeviceIoStateService.ConfirmTimeout;

      // Record the wish first: a command that gets lost on the way must not leave the operator
      // looking at a state nobody asked for. The service shows the wish until the camera reports
      // it, and repeats it until the camera does or the wish expires.
      await stateService.SetDesiredStateAsync(
        request.SourceId, DeviceIoKind.Relay, request.Token, request.Active, lifetime);
      await settingsService.SetRelayOutputStateAsync(
        request.SourceId, request.Token, request.Active, context.CancellationToken);
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
      var mode = FormatMode(request.Mode);
      var idleState = FormatIdleState(request.IdleState);
      await settingsService.SetRelayOutputSettingsAsync(
        request.SourceId, request.Token, mode, idleState, request.DelayMs,
        context.CancellationToken);
      // The camera accepted it: the list carries the new settings at once, and the read inside
      // reconciles them with whatever the camera really stored. Neither can undo the write.
      await catalog.RecordRelaySettingsAsync(
        request.SourceId, request.Token, mode, idleState, request.DelayMs,
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
      // Served from the same presence cache as the relays: one camera read for the first caller,
      // nothing for the rest of the wall.
      var inputs = await catalog.InputsAsync(request.SourceId, context.CancellationToken);
      response.Inputs.AddRange(inputs.Select(i =>
      {
        var input = new DigitalInput
        {
          Token        = i.Token,
          IdleState    = ParseIdleState(i.IdleState),
          HasIdleState = i.IdleState != null,
        };
        if (stateService.TryGetLogicalState(request.SourceId, DeviceIoKind.DigitalInput, i.Token) is { } active)
          input.LiveActive = active;
        return input;
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
      var idleState = FormatIdleState(request.IdleState);
      await settingsService.SetDigitalInputIdleStateAsync(
        request.SourceId, request.Token, idleState, context.CancellationToken);
      // The polarity is part of what the screens show: recording it republishes the contact at
      // once, instead of waiting for the camera's next event.
      await catalog.RecordInputIdleStateAsync(
        request.SourceId, request.Token, idleState, context.CancellationToken);
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
        // The camera's own bearing, already a compass bearing (0 = north, clockwise). A camera on a
        // vehicle turns it as it drives; the map follows it for the cameras that follow themselves.
        if (entry.Orientation?.Yaw is { } yaw) response.HeadingDeg = ((yaw % 360) + 360) % 360;
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  // Every camera of this process, pushed as the camera reports it. The caller (web_srv) merges one
  // of these per node into a single SSE stream, so a monitor wall holds one browser connection
  // rather than one per tile. An empty source_id means the whole node.
  public override async Task StreamDeviceIoStates(
    StreamDeviceIoStatesRequest request,
    IServerStreamWriter<DeviceIoStateUpdate> responseStream,
    ServerCallContext context)
  {
    var ct = context.CancellationToken;
    var filter = string.IsNullOrWhiteSpace(request.SourceId) ? null : request.SourceId;

    // Subscribed before the snapshot is written: a change that lands in between is queued rather
    // than lost, and follows the snapshot it supersedes.
    var queue = Channel.CreateUnbounded<DeviceEvent>(new UnboundedChannelOptions
    {
      SingleReader = true,
      SingleWriter = false,
    });
    void OnEvent(DeviceEvent e) => queue.Writer.TryWrite(e);
    stateService.DeviceEventRaised += OnEvent;

    try
    {
      foreach (var (cameraId, snapshot) in stateService.Snapshots())
      {
        if (filter is not null && !string.Equals(cameraId, filter, StringComparison.Ordinal))
          continue;
        await responseStream.WriteAsync(UpdateOf(cameraId, snapshot, initialized: true, observedAt: ""), ct);
      }

      await foreach (var e in queue.Reader.ReadAllAsync(ct))
      {
        if (filter is not null && !string.Equals(e.CameraId, filter, StringComparison.Ordinal))
          continue;
        // The event says what changed; the cache says what the part is now, including the contact
        // the idle state makes of it.
        if (stateService.TryGetSnapshot(e.CameraId, e.Kind, e.Token) is not { } snapshot)
          continue;
        await responseStream.WriteAsync(
          UpdateOf(e.CameraId, snapshot, e.Initialized, e.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
          ct);
      }
    }
    finally
    {
      stateService.DeviceEventRaised -= OnEvent;
    }
  }

  private static DeviceIoStateUpdate UpdateOf(
    string cameraId, DeviceIoSnapshot snapshot, bool initialized, string observedAt)
  {
    var update = new DeviceIoStateUpdate
    {
      SourceId = cameraId,
      PartKind = snapshot.Kind == DeviceIoKind.Relay ? "relay" : "digital_input",
      Token = snapshot.Token,
      LogicalState = snapshot.LogicalState,
      Initialized = initialized,
      ObservedAt = observedAt,
      Pending = snapshot.Pending,
    };
    if (snapshot.Contact is { } contact)
      update.Contact = contact;
    return update;
  }

  private static RelayMode ParseMode(string? mode) =>
    string.Equals(mode, "bistable", StringComparison.OrdinalIgnoreCase) ? RelayMode.Bistable : RelayMode.Monostable;

  private static string FormatMode(RelayMode mode) =>
    mode == RelayMode.Bistable ? "bistable" : "monostable";

  private static IdleState ParseIdleState(string? idleState) =>
    string.Equals(idleState, "open", StringComparison.OrdinalIgnoreCase) ? IdleState.Open : IdleState.Closed;

  private static string FormatIdleState(IdleState idleState) =>
    idleState == IdleState.Open ? "open" : "closed";

  private static DeviceIoResponse Ok() => new() { Success = true };
  private static DeviceIoResponse Fail(string error) => new() { Success = false, Error = error };
}
