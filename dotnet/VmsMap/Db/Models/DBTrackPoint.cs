using System;
using NetTopologySuite.Geometries;

namespace VmsMap.Db
{
  public record DBTrackPoint : BasePgEntity
  {
    public Guid? object_id { get; set; }

    public DateTime timestamp { get; set; }

    public Geometry figure { get; set; } = null!;
    public double? radius { get; set; }
    public string? zoom_level { get; set; }

    // jsonb
    public string? extra_props { get; set; }
  }
}
