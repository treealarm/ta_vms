using System.Text.Json;
using System.Threading.Channels;
using Dapr.Messaging.PublishSubscribe;
using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsSquareSingleton;

/// Pushes a camera to Square whenever its configuration or armed state changes.
///
/// These pushes used to sit inline in VmsCfg's write handlers, which made the config service know
/// about Square. It does not need to: it already announces both changes — server-config-changed and
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
  private const string ServerConfigChangedTopic = "server-config-changed";
  private const string CameraStateChangedTopic = "camera-state-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly CancellationTokenSource _cts = new();

  // Pushing happens off the delivery path. A message carries every camera of a node — a schedule
  // boundary can flip hundreds at once — and each one costs a round trip to VmsCfg plus one to
  // Square, while the subscription's handling policy drops a message that takes longer than ten
  // seconds. Done inline, the tail of a large batch was simply lost, with no retry.
  private readonly Channel<string> _pending =
    Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

  // What was last pushed for a camera, so an event that changes nothing Square can see costs
  // nothing. Most of them are exactly that: the figure carries the name and the base recording
  // profile, and neither moves when a schedule window opens or an alarm override expires.
  private readonly Dictionary<string, string> _lastPushed = new(StringComparer.Ordinal);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(ServerConfigChangedTopic, HandleConfigChangedAsync, _cts.Token);
    _ = RunAsync(CameraStateChangedTopic, HandleStateChangedAsync, _cts.Token);
    _ = PushLoopAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task PushLoopAsync(CancellationToken ct)
  {
    try
    {
      await foreach (var cameraId in _pending.Reader.ReadAllAsync(ct))
      {
        try
        {
          var resp = await cfgClient.GetCameraAsync(
            new SourceRequest { SourceId = cameraId }, cancellationToken: ct);
          if (!resp.Found || resp.Setup is null)
            continue;

          // The event is a signal, never data: the current camera is read here, so a message that
          // arrives late or out of order still results in the present state being pushed.
          var fingerprint = Fingerprint(resp.Setup);
          if (_lastPushed.TryGetValue(cameraId, out var last) && last == fingerprint)
            continue;

          await SquareCameraPush.PushCameraAsync(square, resp.Setup, producerId.Value);
          _lastPushed[cameraId] = fingerprint;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
          // Dropped from the cache so the next event for this camera retries rather than being
          // skipped as unchanged.
          _lastPushed.Remove(cameraId);
          logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", cameraId);
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  /// Everything SquareCameraPush actually puts into the figure. Anything not in here cannot make
  /// the pushed object differ, so a change to it must not cost a round trip.
  private static string Fingerprint(CameraSetup setup)
  {
    var primary = setup.Streams.FirstOrDefault(s => s.WebRtc);
    return $"{setup.Name}\u001f{primary?.RecordingProfile?.Name ?? string.Empty}";
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

  // Wire shape of server-config-changed; VmsCfg owns it. The server is ignored here — Square holds
  // every node's cameras under one root, so this service is the one subscriber that cares about all
  // of them. An empty camera list means something node-wide changed (a media router, say), which
  // does not alter what Square shows.
  private sealed record ServerConfigChanged(string? ServerId, IReadOnlyList<string>? CameraIds);

  private Task<TopicResponseAction> HandleConfigChangedAsync(TopicMessage message, CancellationToken ct)
  {
    var evt = ReadPayload<ServerConfigChanged>(message);
    if (evt is null)
      return Task.FromResult(TopicResponseAction.Drop);

    foreach (var cameraId in evt.CameraIds ?? [])
      _pending.Writer.TryWrite(cameraId);

    return Task.FromResult(TopicResponseAction.Success);
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

  // camera-state-changed still carries a bare camera id; server-config-changed carries the record
  // above.
  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private T? ReadPayload<T>(TopicMessage message)
  {
    try
    {
      return JsonSerializer.Deserialize<T>(message.Data.Span, JsonOptions);
    }
    catch (JsonException ex)
    {
      logger.LogWarning(ex, "Dropping malformed message");
      return default;
    }
  }

  private string? ReadCameraId(TopicMessage message) => ReadPayload<string>(message);

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
