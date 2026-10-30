using System;

namespace VmsMap.Db
{
  // A named point with a known position, used to calibrate cameras. A tree node as well: its name
  // and its space are the object's, its point is in `geo_objects`.
  public record DBLandmark
  {
    public Guid object_id { get; set; }
    public double height_m { get; set; }
  }
}
