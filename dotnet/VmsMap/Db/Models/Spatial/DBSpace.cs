using System;

namespace VmsMap.Db
{
  // A space is a tree node first: its name and parent live in `objects`, its geometry (the
  // footprint drawn on the parent canvas) in `geo_objects`. This row is the rest of what makes a
  // space a space -- what kind of world it is, its floor and the view it opens on.
  public record DBSpace
  {
    public Guid object_id { get; set; }
    public string kind { get; set; } = null!;
    public bool real_coordinates { get; set; }
    public int ordinal { get; set; }
    public double elevation_m { get; set; }
    public double view_south { get; set; }
    public double view_west { get; set; }
    public double view_north { get; set; }
    public double view_east { get; set; }
  }

  public static class SpaceKinds
  {
    public const string Map = "map";
    public const string Plan = "plan";
  }
}
