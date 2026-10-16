using Grpc.Core;
using Vms.Onvif;

namespace VmsPtzTrackingSingleton;

/// Sends a session's moves to the VmsOnvif instance that shares the camera's network.
public sealed class PtzCommandSender(CameraPtzRouter router, ILogger<PtzCommandSender> logger) : IPtzCommandSender
{
  // Well inside the ONVIF PT3S the camera gives a continuous move: a command that has not landed by
  // now is one the camera would have finished obeying anyway, and holding the session's loop for
  // longer would delay the stop that follows it.
  private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

  public Task<string?> MoveAsync(string cameraId, float pan, float tilt, CancellationToken ct) =>
    SendAsync(cameraId, (client, route) => client.ContinuousMoveAsync(
      new PtzContinuousMoveRequest
      {
        SourceId     = cameraId,
        ProfileToken = route.ProfileToken,
        Pan          = pan,
        Tilt         = tilt,
        Zoom         = 0,
      }, Options(ct)), ct);

  public Task<string?> AimAsync(string cameraId, float pan, float tilt, float zoom, CancellationToken ct) =>
    SendAsync(cameraId, (client, route) => client.AbsoluteMoveAsync(
      new PtzAbsoluteMoveRequest
      {
        SourceId     = cameraId,
        ProfileToken = route.ProfileToken,
        Pan          = pan,
        Tilt         = tilt,
        // Absolute like pan and tilt: OnvifLib puts it on the wire whatever it is, so 0 is not
        // "unchanged" but the wide end -- which is what a slave with no zoom samples gets.
        Zoom         = zoom,
        Speed        = 0.5f,
      }, Options(ct)), ct);

  public Task<string?> GotoPresetAsync(string cameraId, string presetToken, CancellationToken ct) =>
    SendAsync(cameraId, (client, route) => client.GotoPresetAsync(
      new PtzGotoPresetRequest
      {
        SourceId     = cameraId,
        ProfileToken = route.ProfileToken,
        PresetToken  = presetToken,
      }, Options(ct)), ct);

  public Task<string?> StopAsync(string cameraId, CancellationToken ct) =>
    SendAsync(cameraId, (client, route) => client.StopAsync(
      new PtzStopRequest { SourceId = cameraId, ProfileToken = route.ProfileToken }, Options(ct)), ct);

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Deadline), cancellationToken: ct);

  private async Task<string?> SendAsync(
    string cameraId,
    Func<PtzService.PtzServiceClient, CameraPtzRouter.Route, AsyncUnaryCall<PtzResponse>> call,
    CancellationToken ct)
  {
    var route = await router.ResolveAsync(cameraId, ct);
    if (route is null)
      return $"No VmsOnvif instance can reach camera '{cameraId}'";

    try
    {
      var response = await call(router.PtzClient(route.OnvifAppId), route);
      return response.Success ? null : response.Error;
    }
    catch (RpcException ex)
    {
      logger.LogWarning(ex, "PTZ command for camera '{Camera}' failed", cameraId);
      return ex.Status.Detail;
    }
  }
}
