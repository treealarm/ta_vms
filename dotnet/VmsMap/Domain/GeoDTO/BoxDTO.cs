
using System.Collections.Generic;

namespace VmsMap.Domain
{
  public class BoxDTO
  {
    public double[]? wn { get; set; }
    public double[]? es { get; set; }
    public double? zoom { get; set; }
    public List<string>? ids { get; set; }
    public int? count { get; set; }
    public ObjPropsSearchDTO? property_filter { get; set; }
    public List<GeometryDTO>? zone { get; set; }
    public bool not_in_zone { get; set; } = false;
    /// The canvas to ask for instead of the rectangle: everything whose figure is drawn in this
    /// space's coordinates, at any depth of the tree.
    public string? canvas_id { get; set; }
  }
}
