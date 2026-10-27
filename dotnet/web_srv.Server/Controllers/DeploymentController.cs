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
/// an administrator's user name and password. VmsCfg keeps the nodes and makes the packages.
/// </summary>
[ApiController]
[Route("api/deployment")]
// IKeycloakAdminClient is registered only when KEYCLOAK_URL is set (Program.cs).
public class DeploymentController(
  CfgService.CfgServiceClient cfg,
  ILogger<DeploymentController> logger,
  IKeycloakAdminClient? kcAdmin = null) : ControllerBase
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
    return Ok(new DeploymentDto("TreeAlarm VMS", d.Distributed, d.CoreAddress, d.EnrollmentOpen, Environment.MachineName));
  }

  [HttpPost("join")]
  [AllowAnonymous]
  [AllowNotReady]
  public async Task<IActionResult> Join(JoinNodeDto body, CancellationToken ct)
  {
    var deployment = await cfg.GetDeploymentAsync(new Empty(), Options(ct));
    var how = "while enrollment was open";
    // Without Keycloak nothing here asks anyone for a password, and neither does this.
    if (!deployment.EnrollmentOpen && kcAdmin is not null)
    {
      var refusal = string.IsNullOrEmpty(body.Username)
        ? "Enrollment of new nodes is closed: an administrator's user name and password are needed"
        : await CheckAdministratorAsync(body.Username, body.Password ?? "");
      if (refusal is not null)
      {
        logger.LogWarning("Node '{Name}' at {Address} refused: {Reason} (asked from {Remote})",
          body.Name, body.Address, refusal, HttpContext.Connection.RemoteIpAddress);
        return Unauthorized(new { error = refusal });
      }
      how = $"by administrator '{body.Username}'";
    }

    EnrollNodeResponse enrolled;
    try
    {
      enrolled = await EnrollAsync(body, ct);
    }
    catch (RpcException ex) when (IsConflict(ex))
    {
      return Conflict(new { error = ex.Status.Detail });
    }
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
      enrolled = await EnrollAsync(body, ct);
    }
    catch (RpcException ex) when (IsConflict(ex))
    {
      return Conflict(new { error = ex.Status.Detail });
    }
    logger.LogInformation("Package for node '{Name}' at {Address} made by '{User}'",
      enrolled.Node.Name, enrolled.Node.Address, User.Identity?.Name);
    return File(enrolled.Package.ToByteArray(), "application/gzip", $"node-{enrolled.Node.Name}.tar.gz");
  }

  private async Task<EnrollNodeResponse> EnrollAsync(JoinNodeDto body, CancellationToken ct) =>
    await cfg.EnrollNodeAsync(
      new EnrollNodeRequest { Name = body.Name ?? "", Address = body.Address ?? "", MediaMtx = body.MediaMtx }, Options(ct));

  // A name taken by another machine, a node with cameras, a machine that is not a core: what the
  // caller has to resolve, said in VmsCfg's words. A bad name or address is a 400 already
  // (RpcExceptionFilter).
  private static bool IsConflict(RpcException ex) =>
    ex.StatusCode is Grpc.Core.StatusCode.AlreadyExists or Grpc.Core.StatusCode.FailedPrecondition;

  // The password is checked by Keycloak itself, which also counts failures; the role is the realm's,
  // the same one that makes someone an administrator here. Null for an administrator, otherwise why
  // not: a mistyped password and a user without the role need different things from whoever typed it.
  private async Task<string?> CheckAdministratorAsync(string username, string password)
  {
    var realm = Require("KEYCLOAK_REALM_NAME");
    try
    {
      await kcAdmin!.GetTokenAsync(realm, Require("KEYCLOAK_CLIENT_ID"), username, password);
    }
    catch (Exception)
    {
      return "Enrollment of new nodes is closed, and that user name or password is wrong";
    }
    var roles = await kcAdmin.GetUserRolesAsync(realm, username);
    return roles.Any(r => string.Equals(r.Name, RoleConstants.Admin, StringComparison.OrdinalIgnoreCase))
      ? null
      : $"Enrollment of new nodes is closed, and '{username}' is not an administrator";
  }

  private static string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
      ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");
}

public record DeploymentDto(string Product, bool Distributed, string CoreAddress, bool EnrollmentOpen, string Machine);
public record JoinNodeDto(string? Name, string? Address, bool MediaMtx, string? Username, string? Password);
public record EnrollmentDto(bool Open);
public record DeploymentNodeDto(
  string ServerId, string Name, string Address, bool MediaMtx, bool Configured, DateTime EnrolledAt, string Status);
