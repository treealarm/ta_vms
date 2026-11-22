using System.Collections.Concurrent;
using OnvifLib;

namespace VmsOnvif;

/// <summary>
/// The relays and digital inputs a camera has, as its configuration declares them: read from the
/// camera once and served from the presence cache afterwards.
/// </summary>
/// <remarks>
/// The discovery sweep fills the presence on its own schedule; this is the read-through for
/// everything that needs a list before the sweep reaches the camera — the device panel, the monitor
/// tile, the capability flags. A miss reads the camera once and reports it, so every later request
/// is answered from the cache; concurrent misses share that one read, because a wall mounts all of
/// its tiles at once and would otherwise reproduce the burst the cache exists to remove.
/// <para>
/// A settings write goes the other way: the camera accepted new settings, so the cache holds them
/// at once and is then reconciled with what the camera actually stored. The write itself has
/// already succeeded and is never failed by that reconciliation.
/// </para>
/// </remarks>
public sealed class DeviceIoCatalog(
  IDeviceIoPresence presence,
  IDeviceIoReader reader,
  ILogger<DeviceIoCatalog> logger)
{
  // The shared read is bounded here rather than by whoever happened to trigger it: one caller
  // giving up must not fail the read the others are waiting on.
  private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

  // One read per (camera, list) at a time. The entry goes when the read finishes, so a failure is
  // not remembered and the next request tries the camera again.
  private readonly ConcurrentDictionary<
    (string CameraId, DeviceIoKind Kind), Lazy<Task<IReadOnlyList<DeviceDescriptor>>>> _reads = new();

  public Task<IReadOnlyList<DeviceDescriptor>> RelaysAsync(string cameraId, CancellationToken ct) =>
    GetAsync(cameraId, DeviceIoKind.Relay, ct);

  public Task<IReadOnlyList<DeviceDescriptor>> InputsAsync(string cameraId, CancellationToken ct) =>
    GetAsync(cameraId, DeviceIoKind.DigitalInput, ct);

  /// <summary>
  /// The relay list read from the camera now, whatever the cache holds. A command must not be
  /// refused, or be given the wrong lifetime, because the cache has not caught up with a relay the
  /// camera has.
  /// </summary>
  public async Task<IReadOnlyList<DeviceDescriptor>> FreshRelaysAsync(
    string cameraId, CancellationToken ct)
  {
    var version = presence.ListVersion(cameraId, DeviceIoKind.Relay);
    var relays = await reader.GetRelayOutputsAsync(cameraId, ct);
    var devices = relays.Select(DeviceDescriptor.From).ToList();
    // Not awaited: opening the subscription is a round trip of its own, and the caller is about to
    // command the camera anyway.
    _ = presence.ReportDevicesAsync(cameraId, DeviceIoKind.Relay, devices, version);
    return devices;
  }

  /// <summary>
  /// The camera accepted a relay settings write. The written values are in the cache before this
  /// returns, so the panel's reload after saving already shows them; the read that follows corrects
  /// them with what the camera really stored.
  /// </summary>
  public async Task RecordRelaySettingsAsync(
    string cameraId, string token, string mode, string idleState, int delayMs, CancellationToken ct)
  {
    await presence.RecordSettingsAsync(
      cameraId, DeviceIoKind.Relay, new DeviceDescriptor(token, idleState, mode, delayMs));
    await TryReconcileAsync(cameraId, DeviceIoKind.Relay, ct);
  }

  /// <summary>The same for an input's idle state, which is the only setting an input carries.</summary>
  public async Task RecordInputIdleStateAsync(
    string cameraId, string token, string idleState, CancellationToken ct)
  {
    await presence.RecordSettingsAsync(
      cameraId, DeviceIoKind.DigitalInput, new DeviceDescriptor(token, idleState));
    await TryReconcileAsync(cameraId, DeviceIoKind.DigitalInput, ct);
  }

  private async Task<IReadOnlyList<DeviceDescriptor>> GetAsync(
    string cameraId, DeviceIoKind kind, CancellationToken ct)
  {
    if (presence.TryGetDevices(cameraId, kind) is { } cached)
    {
      // The settings read used to be what opened the subscription, as a side effect of reading the
      // camera. Serving from the cache is that job now — and not awaited, so a slow or failing
      // subscription cannot delay the answer or become its error.
      _ = presence.EnsureWatchingAsync(cameraId);
      return cached;
    }

    var key = (cameraId, kind);
    var lazy = _reads.GetOrAdd(
      key,
      _ => new Lazy<Task<IReadOnlyList<DeviceDescriptor>>>(
        () => ReadAndReportAsync(cameraId, kind)));
    _ = ClearWhenDoneAsync(key, lazy);
    return await lazy.Value.WaitAsync(ct);
  }

  /// <summary>
  /// One list read from the camera and reported. The caller takes what the presence holds
  /// afterwards rather than the raw answer: if a settings write moved the list's version while the
  /// read was in flight, the report is dropped and the written configuration is the true one.
  /// </summary>
  private async Task<IReadOnlyList<DeviceDescriptor>> ReadAndReportAsync(
    string cameraId, DeviceIoKind kind, CancellationToken ct = default)
  {
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(ReadTimeout);

    var version = presence.ListVersion(cameraId, kind);
    // The line is for the cold path: a read that reconciles an already known list is routine and
    // must not look like a cache miss in the log.
    if (presence.TryGetDevices(cameraId, kind) is null)
    {
      logger.LogInformation(
        "Camera {CameraId}: the {Kind} list has not been read yet — reading it from the camera",
        cameraId, kind);
    }

    var devices = kind == DeviceIoKind.Relay
      ? (await reader.GetRelayOutputsAsync(cameraId, timeout.Token))
        .Select(DeviceDescriptor.From).ToList()
      : (await reader.GetDigitalInputsAsync(cameraId, timeout.Token))
        .Select(DeviceDescriptor.From).ToList();

    _ = presence.ReportDevicesAsync(cameraId, kind, devices, version);
    return presence.TryGetDevices(cameraId, kind) ?? devices;
  }

  /// <summary>
  /// Reads a list so the cache holds what the camera really stored, and never fails the write that
  /// prompted it: that write has already succeeded, and a camera that does not answer now simply
  /// leaves the recorded values in place.
  /// </summary>
  private async Task TryReconcileAsync(string cameraId, DeviceIoKind kind, CancellationToken ct)
  {
    try
    {
      await ReadAndReportAsync(cameraId, kind, ct);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex,
        "Re-reading camera {CameraId}'s {Kind} list after a settings write failed", cameraId, kind);
    }
  }

  private async Task ClearWhenDoneAsync(
    (string CameraId, DeviceIoKind Kind) key,
    Lazy<Task<IReadOnlyList<DeviceDescriptor>>> lazy)
  {
    try
    {
      await lazy.Value;
    }
    catch
    {
      // The waiters see the failure; the entry must not keep it.
    }
    finally
    {
      _reads.TryRemove(new KeyValuePair<
        (string CameraId, DeviceIoKind Kind), Lazy<Task<IReadOnlyList<DeviceDescriptor>>>>(key, lazy));
    }
  }
}
