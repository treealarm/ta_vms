using Dapr.Messaging.PublishSubscribe;
using System.Text.Json;

namespace web_vms.Authorization;

/// <summary>
/// Keeps <see cref="CameraAccessService"/>'s cache honest: VmsCfg publishes after every permission
/// write, so a grant or revocation lands within a round-trip instead of waiting out the TTL. The
/// payload is only a signal — the username to drop, or empty for "the mode changed, drop
/// everything"; the fresh state is always re-fetched from VmsCfg.
/// </summary>
public sealed class CameraPermissionSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  ICameraAccessService access,
  ILogger<CameraPermissionSubscriber> logger) : IHostedService, IDisposable
{
  private const string CameraPermissionsChangedTopic = "camera-permissions-changed";

  // Read per instance, not into a static field: a missing variable in a static initializer surfaces
  // as a TypeInitializationException from DI, which says nothing about which variable is missing.
  private readonly string _pubSubName =
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
        _pubSubName,
        CameraPermissionsChangedTopic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry)),
        HandleAsync,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "CameraPermissionSubscriber error");
    }
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      access.Invalidate(JsonSerializer.Deserialize<string>(message.Data.Span) ?? string.Empty);
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
