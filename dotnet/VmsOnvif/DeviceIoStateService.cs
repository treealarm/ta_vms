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
/// One part the camera says it has, as its configuration calls it. <paramref name="IdleState"/> is
/// the normal state of the input's contact ("open" or "closed"); null when the camera does not say,
/// and then the contact cannot be computed at all.
/// </summary>
public readonly record struct DeviceDescriptor(string Token, string? IdleState);

/// <summary>
/// What one part just did, in the terms the rest of the system speaks: a contact for an input, an
/// active coil for a relay. <see cref="Previous"/> is the value of the same field before this
/// transition. A null <see cref="Contact"/> with <see cref="PolarityKnown"/> false is the camera
/// having said the input changed but not which contact that is — it is not guessed.
/// </summary>
public sealed record DeviceEvent(
  string CameraId,
  DeviceIoKind Kind,
  string Token,
  string? Contact,
  bool? Active,
  object? Previous,
  bool PolarityKnown,
  bool Initialized,
  DateTimeOffset ObservedAt);

/// <summary>What a part looks like right now, for a device snapshot.</summary>
public readonly record struct DeviceIoSnapshot(
  DeviceIoKind Kind,
  string Token,
  bool LogicalState,
  string? IdleState,
  string? Contact);

/// <summary>
/// The camera's parts that were just forgotten, and therefore are no longer present. Published as
/// a snapshot with <c>present=false</c>: the node and its history stay, only the part is gone.
/// </summary>
public sealed record ForgottenParts(
  string CameraId,
  IReadOnlyList<(DeviceIoKind Kind, DeviceDescriptor Descriptor)> Parts);

/// <summary>
/// What discovery needs from the state service: report what the camera's configuration says it has,
/// and read the last state it announced. Kept apart from the events so discovery can be tested
/// without a watcher.
/// </summary>
public interface IDeviceIoStateSink
{
  Task ReportDevicesAsync(string cameraId, DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices);

  DeviceIoSnapshot? TryGetSnapshot(string cameraId, DeviceIoKind kind, string token);
}

/// <summary>
/// Keeps the live state of every camera's relays and digital inputs, as the camera reports it.
/// </summary>
/// <remarks>
/// ONVIF has no getter for either state: the relays and inputs return configuration, and the state
/// itself travels as <c>Device/Trigger</c> property events. Subscribing replays the current state
/// as <c>Initialized</c>; changes arrive as <c>Changed</c>. This service turns that stream into a
/// cache the gRPC layer can read synchronously, and into <see cref="DeviceEventRaised"/> — one
/// message per transition, which is what the map subscribes to.
/// <para>
/// A subscription is opened the first time the camera is seen to have relays or inputs, and closed
/// once both lists are empty again. The two lists are read by separate calls, so each is remembered
/// on its own: a camera reporting three relays and no inputs must not have its watcher closed by
/// the inputs report that follows.
/// </para>
/// <para>
/// The contact of an input is computed from its logical state and the idle state its configuration
/// declares: <c>closed = idle == "open" ? logical : !logical</c>. When the idle state is unknown the
/// event says so (<c>polarity_known=false</c>) and carries no contact, rather than guessing one.
/// </para>
/// </remarks>
public sealed class DeviceIoStateService : IAsyncDisposable, IDeviceIoStateSink
{
  /// <summary>Rapid flapping of one contact is one event, not a burst.</summary>
  public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

  // Lazy so concurrent reports for one camera open exactly one subscription: ConcurrentDictionary
  // may build several candidates, but only the stored one is ever started.
  private readonly ConcurrentDictionary<string, Lazy<Task<Watch?>>> _watchers = new();
  private readonly ConcurrentDictionary<string, DevicePresence> _presence = new();

  public DeviceIoStateService(
    ICameraConnectionSource connections,
    IDeviceIoWatcherFactory factory,
    ILogger<DeviceIoStateService> logger)
  {
    _connections = connections;
    _factory = factory;
    _logger = logger;
  }

  private readonly ICameraConnectionSource _connections;
  private readonly IDeviceIoWatcherFactory _factory;
  private readonly ILogger<DeviceIoStateService> _logger;

  /// <summary>One transition of one part, ready to be published.</summary>
  public event Action<DeviceEvent>? DeviceEventRaised;

  /// <summary>
  /// The camera's parts are gone from the tree's point of view — the camera was reconfigured or
  /// deleted. The map marks them absent; it never deletes the nodes.
  /// </summary>
  public event Action<ForgottenParts>? PartsForgotten;

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

  public DeviceIoSnapshot? TryGetSnapshot(string cameraId, DeviceIoKind kind, string token)
  {
    if (TryGetLogicalState(cameraId, kind, token) is not { } logical)
      return null;

    var idleState = IdleStateFor(cameraId, kind, token);
    return new DeviceIoSnapshot(
      kind,
      token,
      logical,
      idleState,
      kind == DeviceIoKind.DigitalInput ? Contact(idleState, logical) : null);
  }

  /// <summary>
  /// Records what one of the camera's I/O lists just looked like and brings its subscription in
  /// line: opened when the camera has any device, closed once both lists are empty. A changed idle
  /// state recomputes the contact at once, without waiting for the next heartbeat. Never throws —
  /// its callers are settings reads and discovery, of which live values are an extra.
  /// </summary>
  public async Task ReportDevicesAsync(
    string cameraId, DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices)
  {
    try
    {
      // A watcher that was already there holds a logical state read under the old idle state; a
      // watcher opened by this very report is initialized under the new one and needs no correction.
      var hadWatcher = _watchers.ContainsKey(cameraId);

      DevicePresence? before = null;
      var presence = _presence.AddOrUpdate(
        cameraId,
        _ => DevicePresence.First(kind, devices),
        (_, known) =>
        {
          before = known;
          return known.With(kind, devices);
        });

      if (presence.HasDevices)
        await EnsureWatchingAsync(cameraId);
      else if (presence.IsEmpty)
      {
        await ForgetAsync(cameraId);
        return;
      }

      if (hadWatcher && before is not null)
        await RaisePolarityChangesAsync(cameraId, kind, devices, before.For(kind));
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Bringing camera {CameraId}'s device I/O subscription in line failed", cameraId);
    }
  }

  private async Task RaisePolarityChangesAsync(
    string cameraId,
    DeviceIoKind kind,
    IReadOnlyList<DeviceDescriptor> current,
    IReadOnlyList<DeviceDescriptor> previous)
  {
    if (previous.Count == 0 || !_watchers.TryGetValue(cameraId, out var lazy) || !lazy.IsValueCreated)
      return;

    Watch? watch;
    try { watch = await lazy.Value; }
    catch { return; }
    if (watch is null)
      return;

    foreach (var descriptor in current)
    {
      var old = previous.FirstOrDefault(x => string.Equals(x.Token, descriptor.Token, StringComparison.Ordinal));
      if (old.Token is null)
        continue;
      if (string.Equals(old.IdleState, descriptor.IdleState, StringComparison.OrdinalIgnoreCase))
        continue;

      watch.RaisePolarityChanged(kind, descriptor.Token, old.IdleState);
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
      var data = await _connections.GetAsync(cameraId, CancellationToken.None);
      if (data?.Conn is not { } conn)
        return null;

      var watcher = await _factory.CreateAsync(conn);
      if (watcher is null)
        return null;

      _logger.LogInformation("Watching ONVIF device I/O state for camera {CameraId}", cameraId);
      return new Watch(this, cameraId, watcher);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Opening camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
      return null;
    }
  }

  /// <summary>
  /// Drops the camera's subscription and everything remembered about it. Called when its I/O is
  /// gone, and when its configuration changed: the connection the watcher holds may no longer be
  /// the right one, and the next settings read opens a new subscription on the current one.
  /// <para>
  /// The parts that were known are announced as absent, so the map does not wait for a discovery
  /// sweep to notice that a camera no longer has them.
  /// </para>
  /// </summary>
  public Task ForgetAsync(string cameraId) => ForgetAsync(cameraId, announce: true);

  private async Task ForgetAsync(string cameraId, bool announce)
  {
    if (_presence.TryRemove(cameraId, out var presence) && announce)
    {
      var parts = presence.All();
      if (parts.Count > 0)
        PartsForgotten?.Invoke(new ForgottenParts(cameraId, parts));
    }

    if (!_watchers.TryRemove(cameraId, out var lazy) || !lazy.IsValueCreated)
      return;

    try
    {
      if (await lazy.Value is { } watch)
      {
        await watch.DisposeAsync();
        _logger.LogInformation("Stopped watching ONVIF device I/O state for camera {CameraId}", cameraId);
      }
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Closing camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
    }
  }

  /// <summary>
  /// Drops every subscription — a change that names no camera may have moved or reconfigured any of
  /// them. The parts are not announced one by one: a node-wide change at this scale would turn into
  /// thousands of messages, and discovery re-establishes the manifest on its next sweep.
  /// </summary>
  public async Task ForgetAllAsync()
  {
    _presence.Clear();
    foreach (var cameraId in _watchers.Keys.ToArray())
      await ForgetAsync(cameraId, announce: false);
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
        _logger.LogWarning(ex, "Closing camera {CameraId}'s ONVIF device I/O subscription failed", cameraId);
      }
    }
  }

  private string? IdleStateFor(string cameraId, DeviceIoKind kind, string token) =>
    _presence.TryGetValue(cameraId, out var presence)
      ? presence.Find(kind, token)?.IdleState
      : null;

  // contact_closed = (idle_state == 'open') ? logical : !logical. This is the whole polarity rule,
  // and it lives in one place: the map never re-derives it.
  private static string? Contact(string? idleState, bool logical)
  {
    if (idleState is null)
      return null;

    var closed = string.Equals(idleState, "open", StringComparison.OrdinalIgnoreCase)
      ? logical
      : !logical;
    return closed ? "closed" : "open";
  }

  /// <summary>One camera's subscription and the states it has reported so far.</summary>
  private sealed class Watch : IAsyncDisposable
  {
    private readonly DeviceIoStateService _owner;
    private readonly string _cameraId;
    private readonly IDeviceIoWatcher _watcher;
    private readonly ConcurrentDictionary<(DeviceIoKind Kind, string Token), DeviceIoState> _states = new();
    private readonly ConcurrentDictionary<(DeviceIoKind Kind, string Token), Published> _published = new();
    private readonly ConcurrentDictionary<(DeviceIoKind Kind, string Token), CancellationTokenSource> _debounce = new();
    private readonly object _gate = new();

    public Watch(DeviceIoStateService owner, string cameraId, IDeviceIoWatcher watcher)
    {
      _owner = owner;
      _cameraId = cameraId;
      _watcher = watcher;
      _watcher.StateChanged += OnStateChanged;
      // The Initialized replay can arrive before the handler above is attached.
      foreach (var state in _watcher.States)
        _states[(state.Kind, state.Token)] = state;
    }

    public bool TryGet(DeviceIoKind kind, string token, [NotNullWhen(true)] out DeviceIoState? state) =>
      _states.TryGetValue((kind, token), out state);

    private void OnStateChanged(DeviceIoState state)
    {
      _states[(state.Kind, state.Token)] = state;

      if (string.Equals(state.PropertyOperation, "Initialized", StringComparison.OrdinalIgnoreCase))
      {
        Publish(state.Kind, state.Token, initialized: true);
        return;
      }

      var key = (state.Kind, state.Token);

      // A repeat of the value the map already has is not a transition and costs nothing; the
      // subscription does replay the current value on reconnect.
      if (_published.TryGetValue(key, out var published) && published.Logical == state.LogicalState)
        return;

      DebouncePublish(state.Kind, state.Token);
    }

    // Flapping contacts are debounced per part: the last value within the window is what the map
    // sees. Initialized is not debounced — it is the opening snapshot, not a transition.
    private void DebouncePublish(DeviceIoKind kind, string token)
    {
      var key = (kind, token);
      if (_debounce.TryRemove(key, out var previous))
        previous.Cancel();

      var cts = new CancellationTokenSource();
      _debounce[key] = cts;

      _ = Task.Run(async () =>
      {
        try
        {
          await Task.Delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
          return;
        }

        _debounce.TryRemove(new KeyValuePair<(DeviceIoKind, string), CancellationTokenSource>(key, cts));
        if (!_states.ContainsKey(key))
          return;

        Publish(kind, token, initialized: false);
        cts.Dispose();
      });
    }

    /// <summary>
    /// The camera's idle-state setting changed while the watcher holds its logical state. The
    /// contact is a different value for the same logical state from now on, so the map is told at
    /// once — waiting for the next heartbeat would leave the alarm computed by the old polarity.
    /// </summary>
    public void RaisePolarityChanged(DeviceIoKind kind, string token, string? previousIdleState)
    {
      if (!_states.TryGetValue((kind, token), out var state))
        return;

      var newContact = Contact(_owner.IdleStateFor(_cameraId, kind, token), state.LogicalState);
      var oldContact = Contact(previousIdleState, state.LogicalState);
      if (string.Equals(newContact, oldContact, StringComparison.Ordinal))
        return;

      var key = (kind, token);
      _published.TryGetValue(key, out var published);
      Publish(kind, token, initialized: false, previous: oldContact ?? published?.Value);
    }

    private void Publish(
      DeviceIoKind kind,
      string token,
      bool initialized,
      object? previous = null)
    {
      if (!_states.TryGetValue((kind, token), out var state))
        return;

      var idleState = _owner.IdleStateFor(_cameraId, kind, token);
      var contact = kind == DeviceIoKind.DigitalInput ? Contact(idleState, state.LogicalState) : null;
      var active = kind == DeviceIoKind.Relay ? state.LogicalState : (bool?)null;
      var value = (object?)contact ?? active;

      var key = (kind, token);
      _published.TryGetValue(key, out var published);

      lock (_gate)
      {
        // A transition to the value already published is not a transition. A polarity change is
        // the exception: its caller passes an explicit `previous` that differs.
        if (!initialized && published is not null && published.Logical == state.LogicalState
            && Equals(published.Value, value))
        {
          return;
        }

        var previousValue = previous ?? published?.Value;
        _published[key] = new Published(state.LogicalState, value);

        _owner.DeviceEventRaised?.Invoke(new DeviceEvent(
          _cameraId,
          kind,
          token,
          contact,
          active,
          previousValue,
          PolarityKnown: kind == DeviceIoKind.Relay || idleState is not null,
          initialized,
          DateTimeOffset.UtcNow));
      }
    }

    public ValueTask DisposeAsync()
    {
      _watcher.StateChanged -= OnStateChanged;
      foreach (var cts in _debounce.Values)
        cts.Cancel();
      _debounce.Clear();
      _states.Clear();
      _published.Clear();
      return _watcher.DisposeAsync();
    }

    private sealed record Published(bool Logical, object? Value);
  }

  /// <summary>
  /// What each of a camera's two I/O lists last reported. A null list means "not read yet": the
  /// calls that read them are independent, and only when both have read zero is the camera's I/O
  /// really gone.
  /// </summary>
  private sealed record DevicePresence(
    IReadOnlyList<DeviceDescriptor>? Relays,
    IReadOnlyList<DeviceDescriptor>? Inputs)
  {
    public static DevicePresence First(DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices) =>
      kind == DeviceIoKind.Relay ? new(devices, null) : new(null, devices);

    public DevicePresence With(DeviceIoKind kind, IReadOnlyList<DeviceDescriptor> devices) =>
      kind == DeviceIoKind.Relay ? this with { Relays = devices } : this with { Inputs = devices };

    public IReadOnlyList<DeviceDescriptor> For(DeviceIoKind kind) =>
      kind == DeviceIoKind.Relay ? Relays ?? [] : Inputs ?? [];

    public DeviceDescriptor? Find(DeviceIoKind kind, string token)
    {
      foreach (var descriptor in For(kind))
      {
        if (string.Equals(descriptor.Token, token, StringComparison.Ordinal))
          return descriptor;
      }
      return null;
    }

    public IReadOnlyList<(DeviceIoKind Kind, DeviceDescriptor Descriptor)> All()
    {
      var result = new List<(DeviceIoKind, DeviceDescriptor)>();
      foreach (var descriptor in Relays ?? [])
        result.Add((DeviceIoKind.Relay, descriptor));
      foreach (var descriptor in Inputs ?? [])
        result.Add((DeviceIoKind.DigitalInput, descriptor));
      return result;
    }

    public bool HasDevices => Relays is { Count: > 0 } || Inputs is { Count: > 0 };

    public bool IsEmpty => Relays is { Count: 0 } && Inputs is { Count: 0 };
  }
}
