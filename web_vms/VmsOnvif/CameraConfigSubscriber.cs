using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;

namespace VmsOnvif;

public sealed class CameraConfigSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  OnvifFilterCache filterCache,
  CameraConnCache connCache,
  OnvifEventSubscriptionService subscriptionService) : IHostedService, IDisposable
{
  private const string CameraConfigChangedTopic = "camera-config-changed";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        CameraConfigChangedTopic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry)),
        HandleAsync,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"CameraConfigSubscriber error: {ex}");
    }
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var cameraId = JsonSerializer.Deserialize<string>(message.Data.Span);
      if (!string.IsNullOrWhiteSpace(cameraId))
      {
        connCache.Invalidate(cameraId);
        filterCache.Invalidate(cameraId);
        subscriptionService.NotifyConfigChanged(cameraId);
      }
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch
    {
      return Task.FromResult(TopicResponseAction.Retry);
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
