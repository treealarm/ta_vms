using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsLog;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  // The video half of presence: VmsFs announces that a camera's video state moved, and the map reads
  // the state back instead of trusting the message. The handler only queues the camera — a burst
  // after a media-server restart must not turn into one database transaction per message — and a
  // one-second drain evaluates the set with bounded parallelism.
  internal sealed class CameraPresenceSubscriber : IHostedService, IDisposable
  {
    private const string Topic = "camera-video-changed";
    private const int DrainParallelism = 8;
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(1);

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IServiceScopeFactory _scopes;
    private readonly ICameraCatalog _catalog;
    private readonly CameraPresenceQueue _queue;
    private readonly ILogger<CameraPresenceSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _drain;

    public CameraPresenceSubscriber(
      DaprPublishSubscribeClient pubSub,
      IServiceScopeFactory scopes,
      ICameraCatalog catalog,
      CameraPresenceQueue queue,
      ILogger<CameraPresenceSubscriber> logger)
    {
      _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
      _queue = queue ?? throw new ArgumentNullException(nameof(queue));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [new(Topic, HandleAsync)], _logger, _cts.Token);

      _drain = Task.Run(() => DrainAsync(_cts.Token));
      return Task.CompletedTask;
    }

    private sealed record CameraVideoChanged(string? CameraId);

    private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
    {
      CameraVideoChanged? evt;
      try
      {
        evt = JsonSerializer.Deserialize<CameraVideoChanged>(message.Data.Span, JsonOptions);
      }
      catch (JsonException ex)
      {
        _logger.LogWarning(ex, "Ignoring a malformed {Topic} message", Topic);
        return Task.FromResult(TopicResponseAction.Drop);
      }

      if (evt is null || string.IsNullOrWhiteSpace(evt.CameraId))
        return Task.FromResult(TopicResponseAction.Drop);

      // Acknowledged once queued: a drain that fails is healed by the periodic sweep, which is the
      // safety net for every lost or half-applied decision anyway.
      _queue.Enqueue(evt.CameraId);
      return Task.FromResult(TopicResponseAction.Success);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
      using var timer = new PeriodicTimer(DrainInterval);
      try
      {
        while (await timer.WaitForNextTickAsync(ct))
        {
          var batch = _queue.TakeAll();
          if (batch.Count == 0)
            continue;

          await Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = DrainParallelism, CancellationToken = ct },
            async (cameraId, token) =>
            {
              try
              {
                var camera = await _catalog.GetAsync(cameraId, token);
                if (camera is null)
                  return; // gone from VmsCfg; the config adapter removes the node

                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CameraPresenceService>()
                  .EvaluateAsync(camera, token);
              }
              catch (OperationCanceledException) when (token.IsCancellationRequested)
              {
                throw;
              }
              catch (Exception ex)
              {
                _logger.LogWarning(ex,
                  "Camera presence evaluation failed for '{CameraId}'", cameraId);
              }
            });
        }
      }
      catch (OperationCanceledException)
      {
        // Shutting down.
      }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
      await _cts.CancelAsync();
      if (_drain is not null)
      {
        try { await _drain; } catch (OperationCanceledException) { }
      }
    }

    public void Dispose() => _cts.Dispose();
  }
}
