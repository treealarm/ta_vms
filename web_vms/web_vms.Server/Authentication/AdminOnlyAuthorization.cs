using Microsoft.AspNetCore.Authorization;

namespace web_vms.Authentication
{
  // Registered unconditionally (even when KEYCLOAK_URL is unset) so that controllers can carry
  // [Authorize(Policy = AuthPolicies.AdminOnly)] regardless of whether auth is enabled. When
  // Keycloak isn't configured, the requirement always succeeds — same open access as today.
  public static class AuthPolicies
  {
    public const string AdminOnly = "AdminOnly";
    public const string Authenticated = "Authenticated";
  }

  /// <summary>
  /// The one place that answers "is there an identity at all in this deployment". Everything that
  /// has to stay open when authentication is disabled — the policy handlers here, and the
  /// per-camera permission checks — asks this rather than re-reading the env var.
  /// </summary>
  public static class AuthGate
  {
    public static bool KeycloakEnabled =>
      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEYCLOAK_URL"));
  }

  public class AdminRoleRequirement : IAuthorizationRequirement
  {
  }

  // Any logged-in user, admin or operator. Same KEYCLOAK_URL gate as AdminOnly: with
  // authentication disabled there is nobody to identify, so the requirement passes.
  public class AuthenticatedRequirement : IAuthorizationRequirement
  {
  }

  public class AuthenticatedAuthorizationHandler : AuthorizationHandler<AuthenticatedRequirement>
  {
    protected override Task HandleRequirementAsync(
      AuthorizationHandlerContext context,
      AuthenticatedRequirement requirement)
    {
      if (!AuthGate.KeycloakEnabled || context.User.Identity?.IsAuthenticated == true)
      {
        context.Succeed(requirement);
      }

      return Task.CompletedTask;
    }
  }

  public class AdminRoleAuthorizationHandler : AuthorizationHandler<AdminRoleRequirement>
  {
    protected override Task HandleRequirementAsync(
      AuthorizationHandlerContext context,
      AdminRoleRequirement requirement)
    {
      if (!AuthGate.KeycloakEnabled || context.User.IsInRole(RoleConstants.Admin))
      {
        context.Succeed(requirement);
      }

      return Task.CompletedTask;
    }
  }
}
