using System.Collections.Concurrent;
using System.Threading.Channels;
using Dapr.Client;
using OnvifLib;

namespace VmsOnvif;

/// <summary>
/// Puts the device state service's transitions on the bus, and snapshots on behalf of discovery.
/// </summary>
/// <remarks>
/// Every event carries an <c>epoch</c> — one GUID per process — and a monotonic <c>seq</c> per
/// (camera, kind, token). That pair is what lets the map reject a redelivered or reordered message:
/// comparing clocks would compare VmsMap's own timestamp with another machine's <c>observed_at</c>,
/// and in a distributed deployment that difference produces false rejections.
/// <para>
/// The channel is bounded: a camera storm must not grow the process without limit. A dropped
/// transition is not lost in the end — the next discovery snapshot reconciles the whole camera — but
/// it is logged rather than disappearing silently.
/// </para>
/// </remarks>
public sealed class DeviceEventPublisher : IHostedService, IDeviceSnapshotPublisher, IDisposable
{
  public const string EventTopic = "device-event";
  public const string SnapshotTopic = "device-snapshot";

  private const int QueueCapacity = 4096;

  private readonly DeviceIoStateService _state;
  private readonly DaprClient _dapr;
  private readonly string _pubSubName;
  private readonly ILogger<DeviceEventPublisher> _logger;

  private readonly Channel<DeviceEvent> _events = Channel.CreateBounded<DeviceEvent>(
    new BoundedChannelOptions(QueueCapacity)
    {
      SingleReader = true,
      FullMode = BoundedChannelFullMode.DropWrite,
    });

  private readonly Channel<DeviceSnapshotPayload> _snapshots =
    Channel.CreateBounded<DeviceSnapshotPayload>(new BoundedChannelOptions(QueueCapacity)
    {
      SingleReader = true,
      FullMode = BoundedChannelFullMode.DropWrite,
    });

  private readonly ConcurrentDictionary<(string Camera, DeviceIoKind Kind, string Token), Sequence>
    _sequences = new();

  // One per process. A restart is a new epoch, which is how the map tells "the sequence went back"
  // from "the process that produced it is gone".
  private readonly string _epoch = Guid.NewGuid().ToString();

  private readonly CancellationTokenSource _cts = new();

  public DeviceEventPublisher(
    DeviceIoStateService state,
    DaprClient dapr,
    string pubSubName,
    ILogger<DeviceEventPublisher> logger)
  {
    _state = state;
    _dapr = dapr;
    _pubSubName = pubSubName;
    _logger = logger;

    _state.DeviceEventRaised += Enqueue;
    _state.PartsForgotten += OnPartsForgotten;
  }

  /// <summary>The epoch every message of this process carries.</summary>
  public string Epoch => _epoch;

  public Task PublishAsync(DeviceSnapshotPayload snapshot)
  {
    if (!_snapshots.Writer.TryWrite(snapshot))
      _logger.LogWarning(
        "Device snapshot queue is full — dropping the snapshot of camera {CameraId}", snapshot.SourceId);
    return Task.CompletedTask;
  }

  private void Enqueue(DeviceEvent evt)
  {
    if (!_events.Writer.TryWrite(evt))
      _logger.LogWarning(
        "Device event queue is full — dropping {Kind}/{Token} of camera {CameraId}",
        evt.Kind, evt.Token, evt.CameraId);
  }

  private void OnPartsForgotten(ForgottenParts forgotten)
  {
    // A part that disappeared is not a transition; the whole manifest is restated with present
    // false, leaving the node and its history in the tree.
    var parts = forgotten.Parts
      .Select(p => new DeviceSnapshotPart(
        DeviceJson.KindName(p.Kind),
        p.Descriptor.Token,
        p.Descriptor.IdleState,
        Contact: null,
        Active: null,
        Present: false))
      .ToList();

    EnqueueSnapshot(new DeviceSnapshotPayload(
      "camera", forgotten.CameraId, Reachable: true, DateTimeOffset.UtcNow, Device: null, parts));
  }

  private void EnqueueSnapshot(DeviceSnapshotPayload snapshot)
  {
    if (!_snapshots.Writer.TryWrite(snapshot))
      _logger.LogWarning(
        "Device snapshot queue is full — dropping the snapshot of camera {CameraId}", snapshot.SourceId);
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = PublishEventsAsync(_cts.Token);
    _ = PublishSnapshotsAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task PublishEventsAsync(CancellationToken ct)
  {
    try
    {
      await foreach (var evt in _events.Reader.ReadAllAsync(ct))
      {
        var key = (evt.CameraId, evt.Kind, evt.Token);
        var sequence = _sequences.GetOrAdd(key, _ => new Sequence());

        var payload = new DeviceEventPayload(
          SourceKind: "camera",
          SourceId: evt.CameraId,
          PartKind: DeviceJson.KindName(evt.Kind),
          Token: evt.Token,
          Contact: evt.Contact,
          Active: evt.Active,
          Previous: evt.Previous,
          PolarityKnown: evt.PolarityKnown,
          Initialized: evt.Initialized,
          ObservedAt: evt.ObservedAt,
          Epoch: _epoch,
          Seq: sequence.Next());

        try
        {
          await _dapr.PublishEventAsync(
            _pubSubName, EventTopic, DeviceJson.Element(payload), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          break;
        }
        catch (Exception ex)
        {
          // The next snapshot reconciles the camera; a retry queue for a transition would only
          // delay the newer state behind it.
          _logger.LogWarning(ex,
            "Publishing device-event {Kind}/{Token} of camera {CameraId} failed",
            evt.Kind, evt.Token, evt.CameraId);
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  private async Task PublishSnapshotsAsync(CancellationToken ct)
  {
    try
    {
      await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(ct))
      {
        try
        {
          var wire = new DeviceSnapshotWire(
            snapshot.SourceKind,
            snapshot.SourceId,
            snapshot.Reachable,
            snapshot.ObservedAt,
            _epoch,
            snapshot.Device,
            snapshot.Parts);

          await _dapr.PublishEventAsync(
            _pubSubName, SnapshotTopic, DeviceJson.Element(wire), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          break;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex,
            "Publishing device-snapshot of camera {CameraId} failed", snapshot.SourceId);
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _state.DeviceEventRaised -= Enqueue;
    _state.PartsForgotten -= OnPartsForgotten;
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose()
  {
    _cts.Dispose();
  }

  private sealed record DeviceSnapshotWire(
    string SourceKind,
    string SourceId,
    bool Reachable,
    DateTimeOffset ObservedAt,
    string Epoch,
    DeviceInfoPayload? Device,
    IReadOnlyList<DeviceSnapshotPart> Parts);

  private sealed record DeviceEventPayload(
    string SourceKind,
    string SourceId,
    string PartKind,
    string Token,
    string? Contact,
    bool? Active,
    object? Previous,
    bool PolarityKnown,
    bool Initialized,
    DateTimeOffset ObservedAt,
    string Epoch,
    long Seq);

  private sealed class Sequence
  {
    private long _value;

    public long Next() => Interlocked.Increment(ref _value);
  }
}
