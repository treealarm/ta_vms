using System.Collections.Concurrent;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Fs;

namespace web_vms;

public sealed record FsFanOutResult<T>(string FsId, FsService.FsServiceClient Client, T Value);

public class ServerPairRegistry(CfgService.CfgServiceClient cfg, ILogger<ServerPairRegistry> logger)
{
  private static readonly TimeSpan FanOutTimeout = TimeSpan.FromSeconds(3);
  private static readonly TimeSpan UnreachableCooldown = TimeSpan.FromSeconds(30);

  private IReadOnlyDictionary<string, string>? _serverToFs;
  // Instances that recently failed a fan-out call — skipped for UnreachableCooldown so a
  // configured-but-dead FS doesn't cost every request the full FanOutTimeout.
  private readonly ConcurrentDictionary<string, DateTime> _unreachableSince = new();

  public async Task InitAsync(CancellationToken ct = default)
  {
    var resp = await cfg.ListMediaServersAsync(new ListMediaServersRequest(), cancellationToken: ct);
    _serverToFs = resp.Servers
      .Where(s => !string.IsNullOrWhiteSpace(s.FsId))
      .ToDictionary(s => s.ServerId, s => s.FsId, StringComparer.OrdinalIgnoreCase);
  }

  public FsService.FsServiceClient GetFsClient(string? serverId)
  {
    var map = _serverToFs ?? throw new InvalidOperationException("ServerPairRegistry not initialized");

    if (string.IsNullOrWhiteSpace(serverId))
      throw new InvalidOperationException("serverId must not be empty for FS client lookup");

    if (!map.TryGetValue(serverId, out var fsId) || string.IsNullOrWhiteSpace(fsId))
      throw new InvalidOperationException($"No FS paired for server '{serverId}'");

    return new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
  }

  public IReadOnlyCollection<string> AllFsIds =>
    (_serverToFs ?? throw new InvalidOperationException("ServerPairRegistry not initialized"))
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
      var client = new FsService.FsServiceClient(DaprClient.CreateInvocationInvoker(fsId));
      try
      {
        var value = await call(client, options);
        _unreachableSince.TryRemove(fsId, out _);
        return new FsFanOutResult<T>(fsId, client, value);
      }
      catch (RpcException ex)
      {
        // NotFound is a legitimate "not on this instance" answer and Cancelled means the caller
        // aborted — neither indicates an outage, so they don't trip the cooldown.
        if (ex.StatusCode is not (StatusCode.NotFound or StatusCode.Cancelled))
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
