using System.Threading.Channels;
using Dapr.Messaging.PublishSubscribe;

namespace VmsSquareSingleton;

/// <summary>
/// Runs <see cref="SquareSpaceExport"/> when a space changes (<c>space-config-changed</c>) or when
/// asked to — a placement change arrives on <c>server-config-changed</c>, which
/// <see cref="CameraChangeSubscriber"/> already holds, and it forwards it here.
/// </summary>
/// <remarks>
/// Requests are gathered for a moment before exporting: dragging a footprint or importing a plan
/// sends a burst of events, and one export after the burst says everything they did.
/// </remarks>
public sealed class SpaceExportWorker(
  DaprPublishSubscribeClient pubSubClient,
  SquareSpaceExport export,
  SquareLinkState linkState,
  ILogger<SpaceExportWorker> logger) : IHostedService, IDisposable
{
  private const string SpaceConfigChangedTopic = "space-config-changed";
  private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();
  private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
    new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

  /// <summary>Asks for an export soon; any number of asks before it runs make one.</summary>
  public void Request() => _requests.Writer.TryWrite(true);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = SubscribeAsync(_cts.Token);
    _ = LoopAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task SubscribeAsync(CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        SpaceConfigChangedTopic,
        new DaprSubscriptionOptions(new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop)),
        // The payload only names the space; the export reads everything afresh anyway.
        (_, _) =>
        {
          Request();
          return Task.FromResult(TopicResponseAction.Success);
        },
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "SpaceExportWorker subscription error");
    }
  }

  private async Task LoopAsync(CancellationToken ct)
  {
    try
    {
      await foreach (var _ in _requests.Reader.ReadAllAsync(ct))
      {
        await Task.Delay(Settle, ct);
        try
        {
          await export.ExportAsync(ct);
          linkState.RecordSuccess();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
          linkState.RecordFailure(ex);
          logger.LogWarning(ex, "Square space export failed");
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
