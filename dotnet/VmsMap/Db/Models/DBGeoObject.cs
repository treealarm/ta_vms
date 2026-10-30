using NetTopologySuite.Geometries;
using System;

namespace VmsMap.Db
{
  public record DBGeoObject : BasePgEntity
  {
    public Geometry figure { get; set; } = null!;
    public double? radius { get; set; }
    public string? zoom_level { get; set; }
    // The nearest ancestor canvas -- a space or a mnemoscheme -- in whose coordinates the figure
    // is drawn. Denormalised so that "what is in this area" can run inside one canvas; a plan in
    // made-up coordinates and a mnemoscheme both cross (0, 0), so the numbers alone cannot say.
    public Guid? canvas_id { get; set; }
  }
}
