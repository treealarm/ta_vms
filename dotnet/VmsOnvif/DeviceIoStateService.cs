using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvif;

/// <summary>
/// A camera's live device states, opened on behalf of <see cref="DeviceIoStateService"/>. The
/// production implementation wraps OnvifLib's <see cref="DeviceIoStateWatcher"/>; tests supply
/// their own.
/// </summary>
public interface IDeviceIoWatcher : IAsyncDisposable
{
  /// <summary>
  /// Raised for every state the camera reports, Initialized and Changed alike.
  /// </summary>
  event Action<DeviceIoState>? StateChanged;

  /// <summary>
  /// What the camera has said so far. Read after attaching <see cref="StateChanged"/>: a state can
  /// arrive between the subscription opening and the handler being wired up.
  /// </summary>
  IReadOnlyCollection<DeviceIoState> States { get; }
}

/// <summary>
/// Opens the I/O event subscription for one camera. Null when the camera has no event service to
/// host one — a camera without relays or inputs is expected to say nothing here.
/// </summary>
public interface IDeviceIoWatcherFactory
{
  Task<IDeviceIoWatcher?> CreateAsync(CameraConnInfo conn);
}

/// <summary>
/// Keeps the live state of every camera's relays and digital inputs, as the camera reports it.
/// </summary>
/// <remarks>
/// ONVIF has no getter for either state: the relays and inputs return configuration, and the state
/// itself travels as <c>Device/Trigger</c> property events. Subscribing replays the current state
/// as <c>Initialized</c>; changes arrive as <c>Changed</c>. This service turns that stream into a
/// cache the gRPC layer can read synchronously, since a settings read must not wait on a long poll.
/// <para>
/// A subscription is opened the first time the camera is seen to have relays or inputs, and closed
/// once both lists are empty again. The two lists are read by separate calls, so each is remembered
/// on its own: a camera reporting three relays and no inputs must not have its watcher closed by
/// the inputs report that follows.
/// </para>
/// </remarks>
public sealed class DeviceIoStateService(
  ICameraConnectionSource connections,
  IDeviceIoWatcherFactory factory,
  ILogger<DeviceIoStateService> logger) : IAsyncDisposable
{
  // Lazy so concurrent reports for one camera open exactly one subscription: ConcurrentDictionary
  // may build several candidates, but only the stored one is ever started.
  private readonly ConcurrentDictionary<string, Lazy<Task<Watch?>>> _watchers = new();
  private readonly ConcurrentDictionary<string, Presence> _presence = new();

  /// <summary>
  /// The last logical state the camera reported for one device, or null when it has said nothing
  /// yet — which is not the same as "inactive", and must not be shown as such.
  /// </summary>
  public bool? TryGetLogicalState(string cameraId, DeviceIoKind kind, string token)
  {
    if (!_watchers.TryGetValue(cameraId, out var lazy) || !lazy.IsValueCreated)
      return null;
    return lazy.Value is { IsCompletedSuccessfully: true } started
      && started.Result is { } watch
      && watch.TryGet(kind, token, out var state)
        ? state.LogicalState
        : null;
  }

  /// <summary>
  /// Records what one of the camera's I/O lists just looked like and brings its subscription in
  /// line: opened when the camera has any device, closed once both lists are empty. Never throws —
  /// its caller is a settings read, of which live values are an extra and not the result.
  /// </summary>
  public async Task ReportDevicePresenceAsync(string cameraId, DeviceIoKind kind, int count)
  {
    try
    {
      var presence = _presence.AddOrUpdate(
        cameraId,
        _ => Presence.First(kind, count),
        (_, known) => known.With(kind, count));

      if (presence.HasDevices)
        await EnsureWatchingAsync(cameraId);
      else if (presence.IsEmpty)
        await ForgetAsync(cameraId);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Bringing camera {CameraId}'s device I/O subscription in line failed", cameraId);
    }
  }

  private async Task EnsureWatchingAsync(string cameraId)
  {
    var lazy = _watchers.GetOrAdd(cameraId, id => new Lazy<Task<Watch?>>(() => StartAsync(id)));

    Watch? watch;
    try { watch = await lazy.Value; }
    catch { watch = null; }

    // A camera with no event service, or one that could not be reached, is left unremembered so a
    // later poll tries again instead of caching the failure forever.
    if (watch is null)
      _watchers.TryRemove(new KeyValuePair<string, Lazy<Task<Watch?>>>(cameraId, lazy));
  }

  private async Task<Watch?> StartAsync(string cameraId)
  {
    try
    {
      var data = await connections.GetAsync(cameraId, CancellationToken.None);
      if (data?.Conn is not { } conn)
        return null;

      var watcher = await factory.CreateAsync(conn);
      if (watcher is null)
        return null;

      logger.LogInformation("Watching ONVIF device I/O state for camera {CameraId}", cameraId);
      return new Watch(watcher);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Opening camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
      return null;
    }
  }

  /// <summary>
  /// Drops the camera's subscription and everything remembered about it. Called when its I/O is
  /// gone, and when its configuration changed: the connection the watcher holds may no longer be
  /// the right one, and the next settings read opens a new subscription on the current one.
  /// </summary>
  public async Task ForgetAsync(string cameraId)
  {
    _presence.TryRemove(cameraId, out _);

    if (!_watchers.TryRemove(cameraId, out var lazy) || !lazy.IsValueCreated)
      return;

    try
    {
      if (await lazy.Value is { } watch)
      {
        await watch.DisposeAsync();
        logger.LogInformation("Stopped watching ONVIF device I/O state for camera {CameraId}", cameraId);
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Closing camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
    }
  }

  /// <summary>
  /// Drops every subscription — a change that names no camera may have moved or reconfigured any of
  /// them.
  /// </summary>
  public async Task ForgetAllAsync()
  {
    _presence.Clear();
    foreach (var cameraId in _watchers.Keys.ToArray())
      await ForgetAsync(cameraId);
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var cameraId in _watchers.Keys.ToArray())
    {
      if (!_watchers.TryRemove(cameraId, out var lazy) || !lazy.IsValueCreated)
        continue;
      try
      {
        if (await lazy.Value is { } watch)
          await watch.DisposeAsync();
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Closing camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
      }
    }
  }

  /// <summary>One camera's subscription and the states it has reported so far.</summary>
  private sealed class Watch : IAsyncDisposable
  {
    private readonly IDeviceIoWatcher _watcher;
    private readonly ConcurrentDictionary<(DeviceIoKind Kind, string Token), DeviceIoState> _states = new();

    public Watch(IDeviceIoWatcher watcher)
    {
      _watcher = watcher;
      _watcher.StateChanged += OnStateChanged;
      // The Initialized replay can arrive before the handler above is attached.
      foreach (var state in _watcher.States)
        _states[(state.Kind, state.Token)] = state;
    }

    public bool TryGet(DeviceIoKind kind, string token, [NotNullWhen(true)] out DeviceIoState? state) =>
      _states.TryGetValue((kind, token), out state);

    private void OnStateChanged(DeviceIoState state) => _states[(state.Kind, state.Token)] = state;

    public ValueTask DisposeAsync()
    {
      _watcher.StateChanged -= OnStateChanged;
      _states.Clear();
      return _watcher.DisposeAsync();
    }
  }

  /// <summary>
  /// What each of a camera's two I/O lists last reported. A count of -1 means "not read yet": the
  /// calls that read them are independent, and only when both have said zero is the camera's I/O
  /// really gone.
  /// </summary>
  private readonly record struct Presence(int Relays, int Inputs)
  {
    public static Presence First(DeviceIoKind kind, int count) =>
      kind == DeviceIoKind.Relay ? new(count, -1) : new(-1, count);

    public Presence With(DeviceIoKind kind, int count) =>
      kind == DeviceIoKind.Relay ? this with { Relays = count } : this with { Inputs = count };

    public bool HasDevices => Relays > 0 || Inputs > 0;

    public bool IsEmpty => Relays == 0 && Inputs == 0;
  }
}
