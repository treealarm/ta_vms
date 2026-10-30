using NetTopologySuite.Geometries;

namespace VmsMap.Db
{
  public record DBGeoObject : BasePgEntity
  {
    public Geometry figure { get; set; } = null!;
    public double? radius { get; set; }
    public string? zoom_level { get; set; }
  }
}
