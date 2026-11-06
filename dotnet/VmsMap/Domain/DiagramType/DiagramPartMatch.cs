namespace VmsMap.Domain
{
  /// <summary>
  /// The ways a template's region can find its device part. One per region and never a cascade:
  /// a token, the operator's configured name, or the part's 1-based position among its kind.
  /// </summary>
  public static class DiagramPartMatch
  {
    public const string Token = "token";
    public const string Name = "name";
    public const string Index = "index";

    public static bool IsKnown(string? matchBy) =>
      matchBy is Token or Name or Index;
  }
}
