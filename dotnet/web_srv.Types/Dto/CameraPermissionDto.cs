namespace web_srv.Types.Dto;

/// <summary>A user of the Keycloak realm, as seen by the admin UI.</summary>
public record RealmUserDto
{
  public required string Username { get; init; }
  /// <summary>
  /// Whether the user holds the realm admin role — administrators bypass camera permissions
  /// entirely, so the UI shows that instead of pretending their grants matter. null = not asked
  /// for (see the withRoles parameter) or Keycloak did not answer for this user.
  /// </summary>
  public bool? IsAdmin { get; init; }
  /// <summary>
  /// The realm roles the user holds, when withRoles asked for them. The rights editor offers a
  /// right to a role as well as to a user name, and the roles it can offer are the ones somebody
  /// holds.
  /// </summary>
  public IReadOnlyList<string> Roles { get; init; } = [];
}
