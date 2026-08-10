using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsSquareSingleton;

/// Pushes a camera to Square whenever its configuration or armed state changes.
///
/// These pushes used to sit inline in VmsCfg's write handlers, which made the config service know
/// about Square. It does not need to: it already announces both changes — camera-config-changed and
/// camera-state-changed — for VmsFs and media_server, and this service is simply another subscriber.
/// VmsCfg is left as what it should be, a gRPC interface to its database.
///
/// The event is treated as a signal, never as data: the current camera is fetched from VmsCfg before
/// pushing, so a message that arrives late or out of order still results in the present state.
public sealed class CameraChangeSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  CfgService.CfgServiceClient cfgClient,
  ISquareIntegration square,
  SquareProducerId producerId,
  ILogger<CameraChangeSubscriber> logger) : IHostedService, IDisposable
{
  private const string CameraConfigChangedTopic = "camera-config-changed";
  private const string CameraStateChangedTopic = "camera-state-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(CameraConfigChangedTopic, HandleConfigChangedAsync, _cts.Token);
    _ = RunAsync(CameraStateChangedTopic, HandleStateChangedAsync, _cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(string topic, TopicMessageHandler handler, CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        topic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop)),
        handler,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "CameraChangeSubscriber error on topic '{Topic}'", topic);
    }
  }

  private async Task<TopicResponseAction> HandleConfigChangedAsync(TopicMessage message, CancellationToken ct)
  {
    var cameraId = ReadCameraId(message);
    if (string.IsNullOrWhiteSpace(cameraId))
      return TopicResponseAction.Drop;

    try
    {
      var resp = await cfgClient.GetCameraAsync(new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
      if (!resp.Found || resp.Setup is null)
        return TopicResponseAction.Success;

      await SquareCameraPush.PushCameraAsync(square, resp.Setup, producerId.Value);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", cameraId);
    }
    return TopicResponseAction.Success;
  }

  private async Task<TopicResponseAction> HandleStateChangedAsync(TopicMessage message, CancellationToken ct)
  {
    var cameraId = ReadCameraId(message);
    if (string.IsNullOrWhiteSpace(cameraId))
      return TopicResponseAction.Drop;

    try
    {
      var state = await cfgClient.GetCamStateAsync(
        new GetCamStateRequest { CameraId = cameraId }, cancellationToken: ct);

      await SquareCameraPush.PushPropertiesAsync(square, cameraId, new Dictionary<string, string>
      {
        ["armed"] = state.Armed.ToString(),
      });
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Square property push failed for camera '{CameraId}'", cameraId);
    }
    return TopicResponseAction.Success;
  }

  // Both topics carry a bare JSON string: the camera id.
  private string? ReadCameraId(TopicMessage message)
  {
    try
    {
      return JsonSerializer.Deserialize<string>(message.Data.Span);
    }
    catch (JsonException ex)
    {
      logger.LogWarning(ex, "Dropping malformed camera-change message");
      return null;
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
