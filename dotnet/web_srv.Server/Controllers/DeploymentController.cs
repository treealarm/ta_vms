using Flurl.Http;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using KeycloakAdmin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_srv.Authentication;
using web_srv.Authorization;
using web_srv.Types.Dto;
using RoleConstants = web_srv.Authentication.RoleConstants;

namespace web_srv.Controllers;

/// <summary>
/// The machines of a deployment over several, and how a new one joins: it asks this web for its
/// package. While enrollment is open any machine may; once the administrator closes it, joining takes
/// an administrator's user name and password, as a new package for a node already there always does.
/// VmsCfg keeps the nodes and makes the packages.
/// </summary>
[ApiController]
[Route("api/deployment")]
// IKeycloakAdminClient is registered only when KEYCLOAK_URL is set, TlsEndpoint only when this web
// speaks HTTPS on the LAN (Program.cs).
public class DeploymentController(
  CfgService.CfgServiceClient cfg,
  ILogger<DeploymentController> logger,
  IKeycloakAdminClient? kcAdmin = null,
  TlsEndpoint? tls = null) : ControllerBase
{
  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);

  /// What a machine looking for its core asks every address of its network: anonymous, and nothing
  /// in it a stranger could use. A node's web answers it too, with its core's address.
  [HttpGet]
  [AllowAnonymous]
  [AllowNotReady]
  public async Task<IActionResult> Get(CancellationToken ct)
  {
    var d = await cfg.GetDeploymentAsync(new Empty(), Options(ct));
    // The fingerprint is shown, never trusted from here: a machine joining is told it by whoever set
    // up the core, and checks what it reaches against that.
    return Ok(new DeploymentDto("TreeAlarm VMS", d.Distributed, d.CoreAddress, d.EnrollmentOpen, Environment.MachineName,
      tls?.AuthorityFingerprint));
  }

  /// The deployment's certificate authority, for an operator to trust it in a browser elsewhere. It
  /// is public: the same certificate every connection to this web presents.
  [HttpGet("ca.crt")]
  [AllowAnonymous]
  [AllowNotReady]
  public IActionResult AuthorityCertificate() =>
    tls is null
      ? NotFound(new { error = "This web does not speak HTTPS: it is not part of a deployment over several machines" })
      : File(System.Text.Encoding.ASCII.GetBytes(tls.Authority.ExportCertificatePem() + "\n"),
          "application/x-x509-ca-cert", "treealarm-vms-ca.crt");

  [HttpPost("join")]
  [AllowAnonymous]
  [AllowNotReady]
  public async Task<IActionResult> Join(JoinNodeDto body, CancellationToken ct)
  {
    var deployment = await cfg.GetDeploymentAsync(new Empty(), Options(ct));
    // An administrator's name and password are checked whenever given, open or not: VmsCfg wants an
    // administrator for a node already there. This action never takes its caller for one otherwise,
    // not even without Keycloak, where the web's own admin actions are open to anyone and a name and
    // password are nothing to check.
    var administrator = kcAdmin is not null && !string.IsNullOrEmpty(body.Username);
    if (administrator || !deployment.EnrollmentOpen)
    {
      (int Status, string Error)? refusal = kcAdmin is null
        ? (StatusCodes.Status403Forbidden,
            "Enrollment of new nodes is closed, and without Keycloak there is no administrator's password to check: open it in Administration, Deployment")
        : !administrator
          ? (StatusCodes.Status401Unauthorized,
              "Enrollment of new nodes is closed: an administrator's user name and password are needed")
          : await CheckAdministratorAsync(body.Username!, body.Password ?? "");
      if (refusal is { } r)
      {
        logger.LogWarning("Node '{Name}' at {Address} refused: {Reason} (asked from {Remote})",
          Printable(body.Name), Printable(body.Address), r.Error, HttpContext.Connection.RemoteIpAddress);
        return StatusCode(r.Status, new { error = r.Error });
      }
    }

    EnrollNodeResponse enrolled;
    try
    {
      enrolled = await EnrollAsync(body, administrator, ct);
    }
    catch (RpcException ex) when (IsConflict(ex))
    {
      return Conflict(new { error = ex.Status.Detail });
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
    {
      logger.LogWarning("Node '{Name}' at {Address} refused: {Reason} (asked from {Remote})",
        Printable(body.Name), Printable(body.Address), ex.Status.Detail, HttpContext.Connection.RemoteIpAddress);
      return Unauthorized(new { error = ex.Status.Detail });
    }
    var how = administrator ? $"by administrator '{Printable(body.Username)}'" : "while enrollment was open";
    logger.LogInformation("Node '{Name}' at {Address} joined {How} (asked from {Remote})",
      enrolled.Node.Name, enrolled.Node.Address, how, HttpContext.Connection.RemoteIpAddress);
    return File(enrolled.Package.ToByteArray(), "application/gzip", $"node-{enrolled.Node.Name}.tar.gz");
  }

  [HttpGet("nodes")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> ListNodes(CancellationToken ct)
  {
    var nodes = await cfg.ListNodesAsync(new Empty(), Options(ct));
    var status = (await cfg.ListMediaServersAsync(new ListMediaServersRequest(), Options(ct))).Servers
      .ToDictionary(s => s.ServerId, s => s.Status, StringComparer.Ordinal);
    return Ok(nodes.Nodes.Select(n => new DeploymentNodeDto(
      n.ServerId, n.Name, n.Address, n.MediaMtx, n.Configured, n.EnrolledAt.ToDateTime(),
      status.GetValueOrDefault(n.ServerId, "offline"))));
  }

  [HttpDelete("nodes/{serverId}")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> RemoveNode(string serverId, CancellationToken ct)
  {
    try
    {
      await cfg.RemoveNodeAsync(new RemoveNodeRequest { ServerId = serverId }, Options(ct));
    }
    catch (RpcException ex) when (IsConflict(ex))
    {
      return Conflict(new { error = ex.Status.Detail });
    }
    return Ok(new ApiResponse(true, "ok"));
  }

  [HttpPut("enrollment")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> SetEnrollment(EnrollmentDto body, CancellationToken ct)
  {
    await cfg.SetEnrollmentOpenAsync(new SetEnrollmentOpenRequest { Open = body.Open }, Options(ct));
    logger.LogInformation("Node enrollment {State} by '{User}'", body.Open ? "opened" : "closed", User.Identity?.Name);
    return Ok(new ApiResponse(true, "ok"));
  }

  /// The package as a file, for a node installed without a network path to this web or without
  /// anyone at it (the installer's NodePackage=, distributed.sh join with a file).
  [HttpPost("nodes/package")]
  [Authorize(Policy = AuthPolicies.AdminOnly)]
  public async Task<IActionResult> Package(JoinNodeDto body, CancellationToken ct)
  {
    EnrollNodeResponse enrolled;
    try
    {
      enrolled = await EnrollAsync(body, true, ct);
    }
    catch (RpcException ex) when (IsConflict(ex))
    {
      return Conflict(new { error = ex.Status.Detail });
    }
    logger.LogInformation("Package for node '{Name}' at {Address} made by '{User}'",
      enrolled.Node.Name, enrolled.Node.Address, User.Identity?.Name);
    return File(enrolled.Package.ToByteArray(), "application/gzip", $"node-{enrolled.Node.Name}.tar.gz");
  }

  private async Task<EnrollNodeResponse> EnrollAsync(JoinNodeDto body, bool administrator, CancellationToken ct) =>
    await cfg.EnrollNodeAsync(new EnrollNodeRequest
    {
      Name = body.Name ?? "", Address = body.Address ?? "", MediaMtx = body.MediaMtx, Administrator = administrator,
    }, Options(ct));

  // A name taken by another machine, a node with cameras, a machine that is not a core: what the
  // caller has to resolve, said in VmsCfg's words. A bad name or address is a 400 already
  // (RpcExceptionFilter).
  private static bool IsConflict(RpcException ex) =>
    ex.StatusCode is Grpc.Core.StatusCode.AlreadyExists or Grpc.Core.StatusCode.FailedPrecondition;

  // The password is checked by Keycloak itself, which also counts failures; the role is the realm's,
  // the same one that makes someone an administrator here. Null for an administrator, otherwise why
  // not: a mistyped password and a user without the role need different things from whoever typed it.
  private async Task<(int Status, string Error)?> CheckAdministratorAsync(string username, string password)
  {
    var realm = Require("KEYCLOAK_REALM_NAME");
    try
    {
      await kcAdmin!.GetTokenAsync(realm, Require("KEYCLOAK_CLIENT_ID"), username, password);
    }
    // Keycloak's answer to a wrong password, through Keycloak.Net, which throws Flurl's exception.
    catch (FlurlHttpException ex) when (ex.StatusCode is StatusCodes.Status400BadRequest or StatusCodes.Status401Unauthorized)
    {
      return (StatusCodes.Status401Unauthorized, "That administrator's user name or password is wrong");
    }
    catch (Exception)
    {
      return (StatusCodes.Status503ServiceUnavailable, "Keycloak does not answer: the password cannot be checked now");
    }
    var roles = await kcAdmin.GetUserRolesAsync(realm, username);
    return roles.Any(r => string.Equals(r.Name, RoleConstants.Admin, StringComparison.OrdinalIgnoreCase))
      ? null
      : (StatusCodes.Status401Unauthorized, $"'{Printable(username)}' is not an administrator");
  }

  // What an anonymous caller sent, fit for a log line: VmsCfg checks a name and an address only after
  // they have been logged, and a line break in one would forge a line of its own.
  private static string Printable(string? value) =>
    string.Concat((value ?? "").Take(100).Select(c => char.IsControl(c) ? '?' : c));

  private static string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
}

public record DeploymentDto(
  string Product, bool Distributed, string CoreAddress, bool EnrollmentOpen, string Machine, string? CaFingerprint);
public record JoinNodeDto(string? Name, string? Address, bool MediaMtx, string? Username, string? Password);
public record EnrollmentDto(bool Open);
public record DeploymentNodeDto(
  string ServerId, string Name, string Address, bool MediaMtx, bool Configured, DateTime EnrolledAt, string Status);
