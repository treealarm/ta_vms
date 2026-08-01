using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace web_vms.Controllers;

public class DependencyHealthDto
{
  public required string Name { get; init; }
  public required bool Ok { get; init; }
  /// <summary>
  /// Why it is not ok. Only filled for signed-in callers: the underlying text is Dapr/Npgsql
  /// output naming internal hosts and ports, which an anonymous (and possibly cross-origin) caller
  /// has no business reading. It is always written to the log regardless.
  /// </summary>
  public string? Error { get; init; }
}

public class HealthDto
{
  /// <summary>True when nothing this gateway depends on is currently broken.</summary>
  public required bool Ok { get; init; }
  public required IReadOnlyList<DependencyHealthDto> Dependencies { get; init; }
}

/// <summary>
/// What the gateway can tell about its dependencies right now. Anonymous like the frontend config
/// endpoint: the SPA has to be able to explain an unusable backend before anyone can log in, and a
/// login page that says "config service is down" beats one that just fails.
/// </summary>
[ApiController]
[Route("api/health")]
[AllowAnonymous]
public class HealthController(
  CfgHealthProbe cfgProbe,
  ServerPairRegistry registry,
  ILogger<HealthController> logger) : ControllerBase
{
  [HttpGet]
  public async Task<ActionResult<HealthDto>> Get(CancellationToken ct)
  {
    // Probed live rather than read off the startup latch, so a config service that dies later is
    // reported too — startup success only tells us about the past.
    var cfg = await cfgProbe.CheckAsync(ct);
    var error = cfg.Error ?? (registry.IsInitialized ? null : registry.LastInitError);

    if (!cfg.Ok)
      logger.LogWarning("Health check: vmscfg unavailable: {Error}", error ?? "no detail");

    var showDetail = User.Identity?.IsAuthenticated == true;
    var dependencies = new List<DependencyHealthDto>
    {
      new()
      {
        Name  = "vmscfg",
        Ok    = cfg.Ok,
        Error = cfg.Ok ? null : (showDetail ? error ?? "unavailable" : "unavailable"),
      },
    };

    return Ok(new HealthDto { Ok = dependencies.All(d => d.Ok), Dependencies = dependencies });
  }
}
