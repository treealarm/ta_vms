namespace VmsMap.Domain
{
  // A right is a row on a tree node: a subject (a Keycloak role or a user name) may perform an
  // action on the node and, from M3, on everything below it. The action set is open — view and
  // edit for any node, the camera actions for a camera — so it is a string here, not an enum.
  public record RightDTO
  {
    public string? object_id { get; set; }
    public string? subject { get; set; }
    public string? action { get; set; }
  }
}
