using System;

namespace VmsMap.Db
{
  public record DBMarkerProp : BasePgEntity
  {
    public string prop_name { get; set; } = null!;
    public string? str_val { get; set; }
    public string? visual_type { get; set; }
    public Guid object_id { get; set; }
  }
}
