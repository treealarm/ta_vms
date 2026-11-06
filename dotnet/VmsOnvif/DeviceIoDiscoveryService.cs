using System.Collections.Concurrent;
using Grpc.Core;
using OnvifLib;
using Vms.Cfg;

namespace VmsOnvif;

/// <summary>
/// Walks this node's cameras and asks each one what I/O it has and what state it is in, so that a
/// sensor alarm works without an operator holding the settings panel open.
/// </summary>
/// <remarks>
/// The sweep is also the manifest: one <c>device-snapshot</c> per camera gives the map every part it
/// did not learn from an event, and reconciles a state the map lost while it was down. One message
/// per camera, not per part — at a hundred thousand cameras that is the difference between a few
/// messages and a few thousand per second.
/// <para>
/// The interval is configuration (<c>DEVICE_DISCOVERY_INTERVAL_SECONDS</c>, five minutes by default),
/// not a constant: a minute-long sweep over a large installation is thousands of requests a second.
/// Cameras run in waves with bounded parallelism for the same reason.
/// </para>
/// <para>
/// A failed read changes nothing. <c>present=false</c> is written only from a successful answer that
/// no longer lists a part; a network timeout is the device being unreachable, which the map shows on
/// every node but never treats as a part disappearing. After three consecutive failures the
/// camera is reported as <c>reachable=false</c> — visible, and still not an alarm.
/// </para>
/// </remarks>
public sealed class DeviceIoDiscoveryService : BackgroundService
{
  /// <summary>Cameras per wave, and how many of a wave are asked at once.</summary>
  public const int BatchSize = 500;
  public const int Parallelism = 4;

  private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(10);
  private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(5);
  private const int UnreachableAfter = 3;

  private readonly CfgService.CfgServiceClient _cfg;
  private readonly IDeviceIoReader _reader;
  private readonly IDeviceIoStateSink _state;
  private readonly IDeviceSnapshotPublisher _snapshots;
  private readonly string _ownServerId;
  private readonly TimeSpan _interval;
  private readonly ILogger<DeviceIoDiscoveryService> _logger;

  // Consecutive failed sweeps per camera. A camera that recovers starts again from zero.
  private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);

  public DeviceIoDiscoveryService(
    CfgService.CfgServiceClient cfg,
    IDeviceIoReader reader,
    IDeviceIoStateSink state,
    IDeviceSnapshotPublisher snapshots,
    string ownServerId,
    TimeSpan interval,
    ILogger<DeviceIoDiscoveryService> logger)
  {
    _cfg = cfg;
    _reader = reader;
    _state = state;
    _snapshots = snapshots;
    _ownServerId = ownServerId;
    _interval = interval;
    _logger = logger;
  }

  /// <summary>The configured sweep interval; unset or malformed keeps the five-minute default.</summary>
  public static TimeSpan ConfiguredInterval()
  {
    var raw = Environment.GetEnvironmentVariable("DEVICE_DISCOVERY_INTERVAL_SECONDS");
    return int.TryParse(raw, out var seconds) && seconds > 0
      ? TimeSpan.FromSeconds(seconds)
      : TimeSpan.FromMinutes(5);
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    try
    {
      // Let the node's ordinary event subscriptions settle first: the sweep needs the same camera
      // connections, and nothing depends on it being instantaneous.
      await Task.Delay(StartupDelay, stoppingToken);

      using var timer = new PeriodicTimer(_interval);
      do
      {
        await SweepAsync(stoppingToken);
      }
      while (await timer.WaitForNextTickAsync(stoppingToken));
    }
    catch (OperationCanceledException) { }
  }

  private async Task SweepAsync(CancellationToken ct)
  {
    List<string> cameras;
    try
    {
      var options = new CallOptions(
        deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
      var response = await _cfg.ListCamerasAsync(
        new ListCamerasRequest { ServerId = _ownServerId }, options);

      cameras = response.Cameras
        .Where(c => c.OnvifConn is { Ip: not null, Port: > 0 } && !string.IsNullOrWhiteSpace(c.OnvifConn.Ip))
        .Select(c => c.CameraId)
        .ToList();
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Listing this node's cameras for device I/O discovery failed");
      return;
    }

    // A camera VmsCfg no longer lists costs nothing to forget; without this its entry would stay
    // for the life of the process.
    var current = cameras.ToHashSet(StringComparer.Ordinal);
    foreach (var gone in _failures.Keys.Where(id => !current.Contains(id)).ToList())
      _failures.TryRemove(gone, out _);

    for (var offset = 0; offset < cameras.Count; offset += BatchSize)
    {
      var wave = cameras.Skip(offset).Take(BatchSize).ToList();
      await Parallel.ForEachAsync(
        wave,
        new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
        async (cameraId, token) =>
        {
          try
          {
            await SweepCameraAsync(cameraId, token);
          }
          catch (OperationCanceledException) when (token.IsCancellationRequested)
          {
            throw;
          }
          catch (Exception ex)
          {
            // One camera must not stop the wave: the next sweep tries it again.
            _logger.LogWarning(ex, "Device I/O discovery failed for camera {CameraId}", cameraId);
          }
        });
    }
  }

  /// <summary>
  /// One camera's sweep. Public so a test can drive it without a running timer or a gRPC camera
  /// list; the content is what matters, not the schedule.
  /// </summary>
  public async Task SweepCameraAsync(string cameraId, CancellationToken ct)
  {
    var relayRead = ReadAsync(t => _reader.GetRelayOutputsAsync(cameraId, t), ct);
    var inputRead = ReadAsync(t => _reader.GetDigitalInputsAsync(cameraId, t), ct);
    await Task.WhenAll(relayRead, inputRead);

    var (relaysOk, relays) = await relayRead;
    var (inputsOk, inputs) = await inputRead;

    if (relaysOk)
      await _state.ReportDevicesAsync(cameraId, DeviceIoKind.Relay, Descriptors(relays));
    if (inputsOk)
      await _state.ReportDevicesAsync(cameraId, DeviceIoKind.DigitalInput, Descriptors(inputs));

    // A partial read is a failed sweep. A snapshot built from it would say the other list's parts
    // are gone, and they are not — the camera merely did not answer.
    if (!relaysOk || !inputsOk)
    {
      var failures = _failures.AddOrUpdate(cameraId, 1, (_, count) => count + 1);
      if (failures >= UnreachableAfter)
        await PublishUnreachableAsync(cameraId, ct);

      _logger.LogDebug(
        "Camera {CameraId} did not answer device I/O discovery (relays={Relays}, inputs={Inputs}, failures={Failures})",
        cameraId, relaysOk, inputsOk, failures);
      return;
    }

    _failures.TryRemove(cameraId, out _);
    await PublishSnapshotAsync(cameraId, relays, inputs, ct);
  }

  private async Task PublishSnapshotAsync(
    string cameraId,
    IReadOnlyList<OnvifRelayOutput> relays,
    IReadOnlyList<OnvifDigitalInput> inputs,
    CancellationToken ct)
  {
    var parts = new List<DeviceSnapshotPart>(relays.Count + inputs.Count);

    foreach (var relay in relays)
    {
      var snapshot = _state.TryGetSnapshot(cameraId, DeviceIoKind.Relay, relay.Token);
      parts.Add(new DeviceSnapshotPart(
        Kind: "relay",
        Token: relay.Token,
        IdleState: relay.IdleState,
        Contact: null,
        Active: snapshot?.LogicalState,
        Present: true));
    }

    foreach (var input in inputs)
    {
      var snapshot = _state.TryGetSnapshot(cameraId, DeviceIoKind.DigitalInput, input.Token);
      parts.Add(new DeviceSnapshotPart(
        Kind: "digital_input",
        Token: input.Token,
        IdleState: input.IdleState,
        Contact: snapshot?.Contact,
        Active: null,
        Present: true));
    }

    await _snapshots.PublishAsync(new DeviceSnapshotPayload(
      "camera", cameraId, Reachable: true, DateTimeOffset.UtcNow, parts));

    _logger.LogDebug(
      "Camera {CameraId}: device snapshot with {Count} part(s) published", cameraId, parts.Count);
  }

  private Task PublishUnreachableAsync(string cameraId, CancellationToken ct)
  {
    // Reachable is the only thing this message changes. The parts list is empty on purpose: an
    // unreachable camera has no list to compare against, and only a successful answer may say a
    // part is absent.
    return _snapshots.PublishAsync(new DeviceSnapshotPayload(
      "camera", cameraId, Reachable: false, DateTimeOffset.UtcNow, []));
  }

  private static List<DeviceDescriptor> Descriptors(IReadOnlyList<OnvifRelayOutput> relays) =>
    relays.Select(r => new DeviceDescriptor(r.Token, r.IdleState)).ToList();

  private static List<DeviceDescriptor> Descriptors(IReadOnlyList<OnvifDigitalInput> inputs) =>
    inputs.Select(i => new DeviceDescriptor(i.Token, i.IdleState)).ToList();

  private static async Task<(bool Ok, T Value)> ReadAsync<T>(
    Func<CancellationToken, Task<T>> read, CancellationToken ct)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(ReadTimeout);
    try
    {
      return (true, await read(timeout.Token));
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      throw;
    }
    catch
    {
      // The reason is logged by the reader; what matters here is that the answer is not a list.
      return (false, default!);
    }
  }
}
