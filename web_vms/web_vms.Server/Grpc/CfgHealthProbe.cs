using Grpc.Core;
using Vms.Cfg;

namespace web_vms;

public sealed record CfgHealth(bool Ok, string? Error);

/// <summary>
/// Current reachability of VmsCfg, as opposed to "did startup ever succeed". The distinction
/// matters: the pairing load is a one-way latch, so a config service that dies after startup would
/// otherwise keep reporting healthy while every list in the UI silently empties out.
///
/// Answers from a short-lived cache because the health endpoint is anonymous — an uncached probe
/// would let anyone drive one gRPC call per request into VmsCfg.
/// </summary>
public sealed class CfgHealthProbe(CfgService.CfgServiceClient cfg)
{
  private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

  private readonly SemaphoreSlim _lock = new(1, 1);
  private CfgHealth _last = new(false, "not probed yet");
  private DateTime _probedAtUtc = DateTime.MinValue;

  public async Task<CfgHealth> CheckAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _probedAtUtc < CacheTtl)
      return _last;

    await _lock.WaitAsync(ct);
    try
    {
      if (DateTime.UtcNow - _probedAtUtc < CacheTtl)
        return _last;

      try
      {
        await cfg.ListMediaServersAsync(
          new ListMediaServersRequest(),
          new CallOptions(deadline: DateTime.UtcNow.Add(ProbeTimeout), cancellationToken: ct));
        _last = new CfgHealth(true, null);
      }
      catch (RpcException ex)
      {
        _last = new CfgHealth(false, ex.Status.Detail);
      }
      catch (Exception ex)
      {
        _last = new CfgHealth(false, ex.Message);
      }

      _probedAtUtc = DateTime.UtcNow;
      return _last;
    }
    finally
    {
      _lock.Release();
    }
  }
}
