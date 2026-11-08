namespace VmsMap.Domain
{
  /// <summary>
  /// The ways a template's region can find its device part. One per region and never a cascade:
  /// a token, the operator's configured name, or the part's 1-based position among its kind.
  /// </summary>
  /// <remarks>
  /// The token is the default, and real cameras are why: ONVIF reports a relay or a digital input
  /// as a token and its configuration, with no name at all, so a name can only be the operator's
  /// own label. Position is the fallback for a template whose tokens are not stable.
  /// </remarks>
  public static class DiagramPartMatch
  {
    public const string Token = "token";
    public const string Name = "name";
    public const string Index = "index";

    public static bool IsKnown(string? matchBy) =>
      matchBy is Token or Name or Index;
  }
}
