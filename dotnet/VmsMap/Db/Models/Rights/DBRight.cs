using System;

namespace VmsMap.Db
{
  // A right is a row on a tree node: subject (a role or a user) may perform action on the node.
  // No single id, so no BasePgEntity — the key is the triple.
  public record DBRight
  {
    public Guid object_id { get; set; }
    public string subject { get; set; } = null!;
    public string action { get; set; } = null!;
  }
}
