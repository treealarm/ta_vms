using System.Collections.Concurrent;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Fs;
using Vms.Onvif;

namespace web_vms;

public sealed record FsFanOutResult<T>(string FsId, FsService.FsServiceClient Client, T Value);

public class ServerPairRegistry(CfgService.CfgServiceClient cfg, ILogger<ServerPairRegistry> logger)
{
  private static readonly TimeSpan FanOutTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan UnreachableCooldown = TimeSpan.FromSeconds(30);

  // volatile: ServerPairInitializer replaces this reference every refresh while request threads
  // read it without any lock.
  private volatile IReadOnlyDictionary<string, string>? _serverToFs;
  private volatile IReadOnlyDictionary<string, string>? _serverToOnvif;
  private volatile string? _lastInitError;
  // Instances that recently failed a fan-out call — skipped for UnreachableCooldown so a
  // configured-but-dead FS doesn't cost every request the full FanOutTimeout.
  private readonly ConcurrentDictionary<string, DateTime> _unreachableSince = new();
  private readonly ConcurrentDictionary<string, CallInvoker> _invokers = new(StringComparer.Ordinal);

  /// <summary>False until the server↔FS map has been read from VmsCfg at least once.</summary>
  public bool IsInitialized => _serverToFs is not null;

  /// <summary>Why the last initialization attempt failed, for the health endpoint. Null once it succeeded.</summary>
  public string? LastInitError => _lastInitError;

  public async Task InitAsync(CancellationToken ct = default)
  {
    try
    {
      var resp = await cfg.ListMediaServersAsync(new ListMediaServersRequest(), cancellationToken: ct);
      _serverToFs = resp.Servers
        .Where(s => !string.IsNullOrWhiteSpace(s.FsId))
        .ToDictionary(s => s.ServerId, s => s.FsId, StringComparer.OrdinalIgnoreCase);
      _serverToOnvif = resp.Servers
        .Where(s => !string.IsNullOrWhiteSpace(s.OnvifId))
        .ToDictionary(s => s.ServerId, s => s.OnvifId, StringComparer.OrdinalIgnoreCase);
      _lastInitError = null;
    }
    catch (Exception ex)
    {
      // Kept verbatim: this is the only place the reason (VmsCfg down, its database missing, …)
      // is visible to anything but the log.
      _lastInitError = ex is RpcException rpc ? rpc.Status.Detail : ex.Message;
      throw;
    }
  }

  public FsService.FsServiceClient GetFsClient(string? serverId)
  {
    var map = _serverToFs ?? throw new ServiceNotReadyException(
      "Configuration service (VmsCfg) has not answered yet — see GET /api/health", _lastInitError);

    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for FS client lookup");

    if (!map.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
      throw new InvalidOperationException($"No FS paired for server '{serverId}'");

    return new FsService.FsServiceClient(Invoker(fsId));
  }

  // One invoker per app id, kept for the process lifetime. Each CreateInvocationInvoker builds a
  // GrpcChannel, and nothing disposes the ones handed out per request — on the PTZ path that is a
  // new connection to daprd for every joystick message. The clients wrapping an invoker are cheap.
  private CallInvoker Invoker(string appId) =>
    _invokers.GetOrAdd(appId, static id => DaprClient.CreateInvocationInvoker(id));

  // ONVIF only reaches a camera from its own node's subnet, so a PTZ or snapshot call has to go to
  // the VmsOnvif sitting next to that camera's media server. There is no instance that can serve
  // them all, and no fan-out either: a camera belongs to exactly one node.
  public OnvifService.OnvifServiceClient GetOnvifClient(string? serverId) =>
    new(Invoker(ResolveOnvifId(serverId)));

  public PtzService.PtzServiceClient GetPtzClient(string? serverId) =>
    new(Invoker(ResolveOnvifId(serverId)));

  public DeviceIoService.DeviceIoServiceClient GetDeviceIoClient(string? serverId) =>
    new(Invoker(ResolveOnvifId(serverId)));

  /// For fan-out over AllOnvifIds, where the app id is already known.
  public OnvifService.OnvifServiceClient OnvifClientFor(string onvifId) => new(Invoker(onvifId));

  // For the few operations that are not about one camera: recent-event dumps have to be collected
  // from every node, since each instance only sees its own cameras' events.
  public IReadOnlyCollection<string> AllOnvifIds =>
    (_serverToOnvif ?? throw new ServiceNotReadyException(
      "Configuration service (VmsCfg) has not answered yet — see GET /api/health", _lastInitError))
      .Values.Distinct().ToList();

  private string ResolveOnvifId(string? serverId)
  {
    var map = _serverToOnvif ?? throw new ServiceNotReadyException(
      "Configuration service (VmsCfg) has not answered yet — see GET /api/health", _lastInitError);

    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for ONVIF client lookup");

    if (!map.TryGetValue(serverId, out var onvifId) || string.IsNullOrWhiteSpace(onvifId))
      throw new InvalidOperationException($"No VmsOnvif paired for server '{serverId}'");

    return onvifId;
  }

  // Same "not ready yet" answer as GetFsClient: every fan-out endpoint (clips, retention events,
  // HLS, FS activity) goes through here, and a bare 500 would hide the reason the whole health
  // mechanism exists to report.
  public IReadOnlyCollection<string> AllFsIds =>
    (_serverToFs ?? throw new ServiceNotReadyException(
      "Configuration service (VmsCfg) has not answered yet — see GET /api/health", _lastInitError))
      .Values.Distinct().ToList();

  // Queries every FS instance in parallel with a short deadline and returns the results that came
  // back in time, each paired with the client that produced it (callers like the HLS playlist
  // builder keep talking to the instance that owns the event/clip). A dead instance fails fast
  // and enters a cooldown so it doesn't cost every subsequent request the full FanOutTimeout.
  public async Task<IReadOnlyList<FsFanOutResult<T>>> FanOutFsAsync<T>(
    Func<FsService.FsServiceClient, CallOptions, Task<T>> call, CancellationToken ct)
  {
    var all = AllFsIds;
    var candidates = all
      .Where(id => !_unreachableSince.TryGetValue(id, out var since) || DateTime.UtcNow - since >= UnreachableCooldown)
      .ToList();
    // Never let the cooldown filter everything out — if all instances are marked, probe them all.
    if (candidates.Count == 0)
      candidates = all.ToList();

    var options = new CallOptions(deadline: DateTime.UtcNow.Add(FanOutTimeout), cancellationToken: ct);
    var tasks = candidates.Select(async fsId =>
    {
      var client = new FsService.FsServiceClient(Invoker(fsId));
      try
      {
        var value = await call(client, options);
        _unreachableSince.TryRemove(fsId, out _);
        return new FsFanOutResult<T>(fsId, client, value);
      }
      catch (RpcException ex)
      {
        // NotFound is a legitimate "not on this instance" answer, Cancelled means the caller aborted,
        // and InvalidArgument is a bad-request (client) error — none indicate an outage, so they don't
        // trip the cooldown (a single malformed id must not suppress an instance for everyone).
        if (ex.StatusCode is not (StatusCode.NotFound or StatusCode.Cancelled or StatusCode.InvalidArgument))
        {
          var firstFailure = !_unreachableSince.ContainsKey(fsId);
          _unreachableSince[fsId] = DateTime.UtcNow;
          if (firstFailure)
            logger.LogWarning(
              "FS instance '{FsId}' dropped from fan-out ({Status}) — skipping it for {Cooldown}s; its results are missing from merged responses until it recovers",
              fsId, ex.StatusCode, UnreachableCooldown.TotalSeconds);
        }
        return null;
      }
    });

    return (await Task.WhenAll(tasks)).Where(r => r is not null).Select(r => r!).ToList();
  }
}
