using System;

namespace VmsMap.Db
{
  // Which device part a region of a device template stands for, and how the region finds it.
  //
  // One way per region, chosen by an administrator: a token, a configured name, or the part's
  // 1-based position among its kind once sorted by token. The three are not tried in turn — a
  // region that could match by three rules is a region whose behaviour nobody can predict.
  public record DBDiagramTypePart
  {
    public Guid diagram_type_id { get; set; }
    public string region_key { get; set; } = null!;
    public string part_kind { get; set; } = null!;
    public string match_by { get; set; } = null!;
    public string match_value { get; set; } = null!;
  }
}
