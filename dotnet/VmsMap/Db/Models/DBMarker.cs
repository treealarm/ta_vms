using System;

namespace VmsMap.Db
{
  public record DBMarker : BasePgEntity
  {
    public Guid? parent_id { get; set; }
    // Set on a replica: the node whose states and values this one shows.
    public Guid? owner_id { get; set; }

    public string name { get; set; } = null!;
  }
}
