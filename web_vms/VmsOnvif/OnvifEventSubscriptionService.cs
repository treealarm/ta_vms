using System.Collections.Concurrent;
using System.Threading.Channels;
using EventServiceReference1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using OnvifLib;
using Vms.Cfg;
using Vms.Fs;

namespace VmsOnvif;

public sealed record OnvifEventEntry(DateTime ReceivedAt, string Topic, string PayloadXml);

public sealed class OnvifEventSubscriptionService(
  CfgService.CfgServiceClient cfgClient,
  ServerPairRegistry registry,
  OnvifFilterCache filterCache,
  CameraArmedCache armedCache,
  ObservedTopicsStore observedTopics,
  string ownServerId,
  ILogger<OnvifEventSubscriptionService> logger) : BackgroundService
{
  private const int RingBufferSize = 250;
  private static readonly TimeSpan PeriodicRefreshInterval = TimeSpan.FromSeconds(30);
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan FsTimeout = TimeSpan.FromSeconds(10);

  private readonly ConcurrentDictionary<string, (Queue<OnvifEventEntry> Queue, object Lock)>
    _eventBuffers = new();

  private readonly ConcurrentDictionary<string, CancellationTokenSource>
    _cameraCts = new();

  private readonly Channel<string> _configChangedChannel =
    Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

  public void NotifyConfigChanged(string cameraId) =>
    _configChangedChannel.Writer.TryWrite(cameraId);

  public IReadOnlyList<OnvifEventEntry> GetRecentEvents(string cameraId)
  {
    if (!_eventBuffers.TryGetValue(cameraId, out var buf))
      return [];
    lock (buf.Lock)
      return [.. buf.Queue.Reverse()];
  }

  public IReadOnlyList<(string CameraId, OnvifEventEntry Entry)> GetAllRecentEvents()
  {
    var result = new List<(string, OnvifEventEntry)>();
    foreach (var (cameraId, buf) in _eventBuffers)
    {
      lock (buf.Lock)
        result.AddRange(buf.Queue.Select(e => (cameraId, e)));
    }
    result.Sort((a, b) => b.Item2.ReceivedAt.CompareTo(a.Item2.ReceivedAt));
    return result;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await RefreshAllCamerasAsync(stoppingToken);
    await Task.WhenAll(
      PeriodicRefreshAsync(stoppingToken),
      ConfigChangedLoopAsync(stoppingToken));
  }

  private async Task PeriodicRefreshAsync(CancellationToken ct)
  {
    using var timer = new PeriodicTimer(PeriodicRefreshInterval);
    while (!ct.IsCancellationRequested)
    {
      try
      {
        await timer.WaitForNextTickAsync(ct);
        await RefreshAllCamerasAsync(ct);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception ex) { logger.LogError(ex, "Periodic camera refresh failed"); }
    }
  }

  private async Task ConfigChangedLoopAsync(CancellationToken ct)
  {
    try
    {
      await foreach (var cameraId in _configChangedChannel.Reader.ReadAllAsync(ct))
        await RefreshCameraAsync(cameraId, ct, forceRestart: true);
    }
    catch (OperationCanceledException) { }
  }

  private async Task RefreshAllCamerasAsync(CancellationToken ct)
  {
    try
    {
      var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
      // Scoped to this node. ONVIF reaches a camera over the same local subnet RTSP does, so another
      // node's cameras are not merely someone else's business — they have no route from here, and
      // subscribing to them would produce nothing but connection timeouts.
      var resp = await cfgClient.ListCamerasAsync(new ListCamerasRequest { ServerId = ownServerId }, opts);
      // ListCameras already carries the full CameraSetup, onvif_conn and onvif_events_enabled
      // included, so the list is applied as-is: a GetCamera per camera would add a round trip per
      // camera on every refresh interval and return what we are already holding.
      foreach (var cam in resp.Cameras)
        ApplyCameraSetup(cam.CameraId, cam, forceRestart: false);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to list cameras for ONVIF subscriptions");
    }
  }

  /// Re-reads one camera. Only the config-changed path needs this — the periodic sweep works off
  /// the list it has already fetched.
  private async Task RefreshCameraAsync(string cameraId, CancellationToken ct, bool forceRestart = false)
  {
    try
    {
      var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
      var resp = await cfgClient.GetCameraAsync(new SourceRequest { SourceId = cameraId }, opts);
      ApplyCameraSetup(cameraId, resp.Found ? resp.Setup : null, forceRestart);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to refresh camera {CameraId} ONVIF subscription", cameraId);
    }
  }

  /// Brings the camera's subscription in line with its config. setup is null when the camera is
  /// gone or unreadable, which stops the subscription just like ONVIF events being switched off.
  private void ApplyCameraSetup(string cameraId, CameraSetup? setup, bool forceRestart)
  {
    try
    {
      bool hasOnvif = setup?.OnvifEventsEnabled == true
        && setup.OnvifConn != null
        && !string.IsNullOrEmpty(setup.OnvifConn.Ip)
        && setup.OnvifConn.Port > 0;

      if (!hasOnvif)
      {
        StopCameraSubscription(cameraId);
        return;
      }

      if (forceRestart)
        StopCameraSubscription(cameraId);

      if (_cameraCts.ContainsKey(cameraId))
        return;

      var cts = new CancellationTokenSource();
      if (_cameraCts.TryAdd(cameraId, cts))
        _ = RunCameraSubscriptionAsync(cameraId, setup!, cts.Token);
      else
        cts.Dispose();
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to apply ONVIF subscription state for camera {CameraId}", cameraId);
    }
  }

  private void StopCameraSubscription(string cameraId)
  {
    if (_cameraCts.TryRemove(cameraId, out var cts))
    {
      cts.Cancel();
      cts.Dispose();
    }
  }

  private async Task RunCameraSubscriptionAsync(
    string cameraId, CameraSetup setup, CancellationToken ct)
  {
    var ip   = setup.OnvifConn!.Ip;
    var port = setup.OnvifConn.Port;
    var user = setup.Cred?.User ?? string.Empty;
    var pass = setup.Cred?.Pass ?? string.Empty;

    logger.LogInformation(
      "Starting ONVIF event subscription for {CameraId} ({Ip}:{Port})", cameraId, ip, port);

    await observedTopics.LoadAsync(cameraId, ct);

    while (!ct.IsCancellationRequested)
    {
      OnvifLib.EventService1? eventSvc = null;
      try
      {
        var camera = Camera.Create(ip, port, user, pass, logger: new OnvifLoggerAdapter(logger));
        eventSvc = await camera.GetEventService();
        if (eventSvc == null)
        {
          logger.LogWarning(
            "Camera {CameraId}: ONVIF event service unavailable, retrying in 30s", cameraId);
          await Task.Delay(TimeSpan.FromSeconds(30), ct);
          continue;
        }

        eventSvc.PullTimeout = TimeSpan.FromSeconds(setup.OnvifPollTimeoutSeconds > 0 ? setup.OnvifPollTimeoutSeconds : 5);
        eventSvc.MessageLimit = setup.OnvifMessageLimit > 0 ? setup.OnvifMessageLimit : 1024;

        eventSvc.OnEventReceived += messages => OnEventsReceived(cameraId, messages ?? []);
        await eventSvc.StartReceivingAsync();
        await Task.Delay(Timeout.Infinite, ct);
      }
      catch (OperationCanceledException) { break; }
      catch (Exception ex)
      {
        logger.LogError(ex,
          "ONVIF subscription error for {CameraId}, retrying in 15s", cameraId);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
      }
      finally
      {
        if (eventSvc != null)
        {
          try { await eventSvc.StopReceivingAsync(); } catch { }
          eventSvc.Dispose();
        }
      }
    }

    _cameraCts.TryRemove(cameraId, out _);
    logger.LogInformation("ONVIF event subscription stopped for {CameraId}", cameraId);
  }

  private void OnEventsReceived(string cameraId, NotificationMessageHolderType[] messages)
  {
    if (messages.Length == 0) return;

    foreach (var msg in messages)
    {
      var topic = ExtractTopicText(msg.Topic);
      var payload = msg.Message?.OuterXml ?? string.Empty;

      logger.LogDebug("Camera {CameraId}: ONVIF event received — topic='{Topic}'", cameraId, topic);

      var record = new OnvifEventEntry(DateTime.UtcNow, topic, payload);

      var buf = _eventBuffers.GetOrAdd(cameraId, _ => (new Queue<OnvifEventEntry>(), new object()));
      lock (buf.Lock)
      {
        buf.Queue.Enqueue(record);
        while (buf.Queue.Count > RingBufferSize)
          buf.Queue.Dequeue();
      }

      observedTopics.Record(cameraId, topic);

      // "Initialized" messages are a state snapshot the camera replays on every (re-)subscribe,
      // not a new state transition — skip alarm triggering to avoid duplicate alarms on resubscribe.
      if (msg.Message?.GetAttribute("PropertyOperation") == "Initialized")
      {
        logger.LogDebug(
          "Camera {CameraId}: skipping alarm check for 'Initialized' snapshot — topic='{Topic}'", cameraId, topic);
        continue;
      }

      _ = Task.Run(() => CheckAndTriggerAlarmAsync(cameraId, topic, payload));
    }
  }

  private async Task CheckAndTriggerAlarmAsync(string cameraId, string topic, string payload)
  {
    try
    {
      List<OnvifAlarmFilter> filters;
      using (var cfgCts = new CancellationTokenSource(CfgTimeout))
        filters = await filterCache.GetAsync(cameraId, cfgCts.Token);

      logger.LogDebug(
        "Camera {CameraId}: checking {Count} filter(s) against topic='{Topic}'",
        cameraId, filters.Count, topic);

      if (filters.Count == 0) return;

      var matched = filters.FirstOrDefault(f =>
        topic.Contains(f.FilterText, StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrEmpty(f.TextFilter) ||
         payload.Contains(f.TextFilter, StringComparison.OrdinalIgnoreCase)));

      if (matched == null)
      {
        logger.LogDebug(
          "Camera {CameraId}: no filter matched topic='{Topic}' (filters: {Filters})",
          cameraId, topic, string.Join(", ", filters.Select(f =>
            string.IsNullOrEmpty(f.TextFilter) ? $"'{f.FilterText}'" : $"'{f.FilterText}'+'{f.TextFilter}'")));
        return;
      }

      using var armedCts = new CancellationTokenSource(CfgTimeout);
      var armed = await armedCache.GetAsync(cameraId, armedCts.Token);
      if (!armed)
      {
        logger.LogDebug(
          "Camera {CameraId}: filter '{Filter}' matched but camera is not armed — skipping retention",
          cameraId, matched.FilterText);
        return;
      }

      logger.LogInformation(
        "Camera {CameraId}: ONVIF alarm triggered by filter '{Filter}'",
        cameraId, matched.FilterText);

      var req = new RetainEventRequest
      {
        EventId        = $"onvif-{Guid.NewGuid()}",
        CameraId       = cameraId,
        AlarmStartTs   = Timestamp.FromDateTime(DateTime.UtcNow),
        TimeoutSeconds = (uint)Math.Max(1, matched.TimeoutSeconds),
      };

      // No lookup of the camera's server: this instance only ever subscribes to its own node's
      // cameras, so the archive that must retain the footage is its own node's VmsFs.
      var fsClient = registry.GetFsClient();
      var fsOpts = new CallOptions(deadline: DateTime.UtcNow.Add(FsTimeout));
      var status = await fsClient.RetainEventAsync(req, fsOpts);
      logger.LogInformation(
        "Camera {CameraId}: RetainEvent returned {Status}", cameraId, status.Status);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "CheckAndTriggerAlarmAsync failed for {CameraId}", cameraId);
    }
  }

  private static string ExtractTopicText(TopicExpressionType? topic)
  {
    if (topic?.Any == null) return string.Empty;
    return string.Join(" ", topic.Any.Select(n => n.InnerText ?? n.OuterXml ?? string.Empty)).Trim();
  }

  public override async Task StopAsync(CancellationToken cancellationToken)
  {
    foreach (var cts in _cameraCts.Values)
    {
      cts.Cancel();
      cts.Dispose();
    }
    _cameraCts.Clear();
    await base.StopAsync(cancellationToken);
  }
}
