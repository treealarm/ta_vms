using Microsoft.AspNetCore.Authorization;

namespace web_vms.Authentication
{
  // Registered unconditionally (even when KEYCLOAK_URL is unset) so that controllers can carry
  // [Authorize(Policy = AuthPolicies.AdminOnly)] regardless of whether auth is enabled. When
  // Keycloak isn't configured, the requirement always succeeds — same open access as today.
  public static class AuthPolicies
  {
    public const string AdminOnly = "AdminOnly";
  }

  public class AdminRoleRequirement : IAuthorizationRequirement
  {
  }

  public class AdminRoleAuthorizationHandler : AuthorizationHandler<AdminRoleRequirement>
  {
    protected override Task HandleRequirementAsync(
      AuthorizationHandlerContext context,
      AdminRoleRequirement requirement)
    {
      var keycloakEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEYCLOAK_URL"));

      if (!keycloakEnabled || context.User.IsInRole(RoleConstants.Admin))
      {
        context.Succeed(requirement);
      }

      return Task.CompletedTask;
    }
  }
}
