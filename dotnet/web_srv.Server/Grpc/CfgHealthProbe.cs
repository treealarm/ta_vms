using Grpc.Core;
using Vms.Cfg;

namespace web_srv;

/// <param name="Starting">
/// Not reachable, but not broken either: this gateway has never yet had an answer from VmsCfg and
/// is still young enough that it should not have expected one. See <see cref="CfgHealthProbe"/>.
/// </param>
public sealed record CfgHealth(bool Ok, string? Error, bool Starting = false);

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

  /// <summary>
  /// How long a VmsCfg that has never answered still counts as starting rather than broken.
  ///
  /// VmsCfg migrates its database before it listens, and the wait for Postgres inside that retries
  /// on a five-second cadence — so from a cold start it is routinely half a minute between the two
  /// processes launching and the first call being accepted. Everything launched together says
  /// "unavailable" during that window, which is true and useless. After it, silence is a fault.
  /// </summary>
  private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

  private readonly SemaphoreSlim _lock = new(1, 1);
  private readonly DateTime _startedAtUtc = DateTime.UtcNow;
  private (bool Ok, string? Error) _last = (false, "not probed yet");
  private DateTime _probedAtUtc = DateTime.MinValue;
  private bool _everOk;

  /// <summary>
  /// Decided on the way out rather than stored with the probe, so a result held in the cache does
  /// not keep claiming "starting" for five seconds after the window shut.
  /// </summary>
  private CfgHealth Classify((bool Ok, string? Error) probe) =>
    probe.Ok
      ? new CfgHealth(true, null)
      : new CfgHealth(false, probe.Error,
          Starting: !_everOk && DateTime.UtcNow - _startedAtUtc < StartupGrace);

  public async Task<CfgHealth> CheckAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _probedAtUtc < CacheTtl)
      return Classify(_last);

    await _lock.WaitAsync(ct);
    try
    {
      if (DateTime.UtcNow - _probedAtUtc < CacheTtl)
        return Classify(_last);

      try
      {
        await cfg.ListMediaServersAsync(
          new ListMediaServersRequest(),
          new CallOptions(deadline: DateTime.UtcNow.Add(ProbeTimeout), cancellationToken: ct));
        _last = (true, null);
        _everOk = true;
      }
      catch (RpcException ex)
      {
        _last = (false, ex.Status.Detail);
      }
      catch (Exception ex)
      {
        _last = (false, ex.Message);
      }

      _probedAtUtc = DateTime.UtcNow;
      return Classify(_last);
    }
    finally
    {
      _lock.Release();
    }
  }
}
