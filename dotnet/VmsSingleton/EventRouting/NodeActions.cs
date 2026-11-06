using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Vms.Fs;
using Vms.Onvif;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The actions that reach a camera's own node: a retention episode in its VmsFs and a relay in its
/// VmsOnvif. Which of the node's services to call comes from VmsCfg's registry, never from a
/// deployment-wide app id — a camera lives on one machine, and its video stays there.
/// </summary>
public sealed class NodeActions(MediaServerRegistry servers) : INodeActions
{
  private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

  public async Task RetainRecordingAsync(
    string cameraId, RetainRecordingCommand command, CancellationToken ct)
  {
    var node = await Require(cameraId, ct);

    var client = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(node.FsId));
    var response = await client.RetainEventAsync(
      new RetainEventRequest
      {
        EventId = command.EventId,
        CameraId = cameraId,
        AlarmStartTs = Timestamp.FromDateTime(
          DateTime.SpecifyKind(command.AlarmStartTs, DateTimeKind.Utc)),
        TimeoutSeconds = (uint)command.TimeoutSeconds,
        RaiseAlarm = command.RaiseAlarm,
      },
      deadline: DateTime.UtcNow.Add(CallTimeout),
      cancellationToken: ct);

    if (response.Status != Vms.Fs.OperationStatus.Types.Status.Success)
      throw new InvalidOperationException(
        $"RetainEvent for camera '{cameraId}' failed ({response.Status}): {response.Message}");
  }

  public async Task SetRelayAsync(
    string cameraId, string token, bool active, CancellationToken ct)
  {
    var node = await Require(cameraId, ct);

    var client = new DeviceIoService.DeviceIoServiceClient(
      DaprClient.CreateInvocationInvoker(node.OnvifId));
    var response = await client.SetRelayOutputStateAsync(
      new SetRelayOutputStateRequest { SourceId = cameraId, Token = token, Active = active },
      deadline: DateTime.UtcNow.Add(CallTimeout),
      cancellationToken: ct);

    if (!response.Success)
      throw new InvalidOperationException(
        $"SetRelayOutputState for camera '{cameraId}' relay '{token}' failed: {response.Error}");
  }

  private async Task<NodeServices> Require(string cameraId, CancellationToken ct) =>
    await servers.ForCameraAsync(cameraId, ct)
    ?? throw new InvalidOperationException(
      $"Camera '{cameraId}' is not in VmsCfg's node registry");
}
