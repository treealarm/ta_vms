namespace web_srv.Authentication
{
  public static class RoleConstants
  {
    public const string Admin = "admin";
    public const string User = "user";
    // Held by an account that must set a new password before anything else, such as the initial
    // admin/admin of a standalone install. A role rather than Keycloak's own "temporary password":
    // Keycloak refuses the password grant outright for one of those, and that grant is how the SPA
    // logs in. AuthController.ChangePassword takes it away; PasswordChangeGateMiddleware enforces it.
    public const string PasswordChangeRequired = "password-change-required";
  }
}
