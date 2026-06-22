using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace web_vms.Authentication
{
  public static class ConfigureAuthExtensions
  {
    // JwtBearerOptions.Authority drives ASP.NET's standard OIDC metadata discovery, which fetches
    // and caches the realm's JWKS signing keys (and rotates them) automatically. No custom
    // key-fetch/warm-up service needed.
    public static void AddKeycloakAuth(
      this IServiceCollection services,
      string keycloakUrl,
      string realmName)
    {
      services.AddTransient<Microsoft.AspNetCore.Authentication.IClaimsTransformation, ClaimsTransformer>();

      services.AddAuthentication(options =>
      {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
      })
      .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
      {
        options.Authority = $"{keycloakUrl.TrimEnd('/')}/realms/{realmName}";
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters.ValidateAudience = false;
      });

      services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
          .RequireAuthenticatedUser()
          .Build());
    }
  }
}
