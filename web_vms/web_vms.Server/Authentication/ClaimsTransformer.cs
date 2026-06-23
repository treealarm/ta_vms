using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using System.Text.Json;

namespace web_vms.Authentication
{
  // Keycloak puts realm roles in a nested "realm_access": { "roles": [...] } claim, which the
  // default JWT bearer mapping does not flatten. This transformer maps them onto ClaimTypes.Role
  // so that [Authorize(Roles = ...)] works.
  public class ClaimsTransformer : IClaimsTransformation
  {
    private class RealmAccess
    {
      public List<string> roles { get; set; } = new();
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
      var identity = (ClaimsIdentity?)principal.Identity;
      if (identity == null || !identity.IsAuthenticated)
      {
        return Task.FromResult(principal);
      }

      var realmAccessClaim = identity.FindFirst("realm_access");
      if (realmAccessClaim != null)
      {
        RealmAccess? realmAccess = null;
        try
        {
          realmAccess = JsonSerializer.Deserialize<RealmAccess>(realmAccessClaim.Value);
        }
        catch (JsonException)
        {
          // Unexpected realm_access shape — treat as no extra roles rather than failing the request.
        }

        foreach (var role in realmAccess?.roles ?? Enumerable.Empty<string>())
        {
          if (!identity.HasClaim(ClaimTypes.Role, role))
          {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
          }
        }
      }

      var username = identity.FindFirst("preferred_username")?.Value;
      if (!string.IsNullOrEmpty(username) && !identity.HasClaim(ClaimTypes.Name, username))
      {
        identity.AddClaim(new Claim(ClaimTypes.Name, username));
      }

      return Task.FromResult(principal);
    }
  }
}
