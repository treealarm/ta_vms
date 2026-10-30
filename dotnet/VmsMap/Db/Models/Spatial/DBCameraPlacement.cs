using System;

namespace VmsMap.Db
{
  // Where a camera is drawn and which way it looks. The position is the node's point in
  // `geo_objects`; a camera may be drawn in several spaces, and exactly one placement -- the
  // reference -- says where it really stands. The others are pictures of it: replicas of the same
  // camera node, with rows of their own for how the icon and its view cone are drawn.
  public record DBCameraPlacement
  {
    public Guid object_id { get; set; }
    // The camera id in VmsCfg; not a foreign key, since cameras live in another database.
    public string camera_id { get; set; } = null!;
    public bool is_reference { get; set; }
    public double height_m { get; set; }
    // Compass bearing: 0 = north, clockwise.
    public double heading_deg { get; set; }
    public double hfov_deg { get; set; }
    public double range_m { get; set; }
  }
}
