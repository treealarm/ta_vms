namespace web_srv.Authentication;

/// <summary>
/// Turns away every API call from an account that must change its password, except the few it
/// needs to do so. The SPA shows only the change-password form to such an account, but that is a
/// courtesy: the token works against the API all the same, and this is what stops it.
/// </summary>
public sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
  private static readonly PathString[] Open =
  [
    "/api/auth/login",
    "/api/auth/refresh",
    "/api/auth/change-password",
    "/api/auth/me",
    "/api/config/frontend",
  ];

  public async Task InvokeAsync(HttpContext context)
  {
    var path = context.Request.Path;
    if (context.User.IsInRole(RoleConstants.PasswordChangeRequired)
      && path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
      && !Open.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
    {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      await context.Response.WriteAsJsonAsync(new { error = "password_change_required" });
      return;
    }

    await next(context);
  }
}
