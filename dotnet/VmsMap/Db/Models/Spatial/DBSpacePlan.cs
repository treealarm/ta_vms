using System;

namespace VmsMap.Db
{
  // A plan space's picture and where it lies in that space. A table of its own so that listing
  // spaces never reads the bytes; etag is the picture's sha256, hex, and its HTTP ETag.
  public record DBSpacePlan
  {
    public Guid space_id { get; set; }
    public string mime { get; set; } = null!;
    public int width_px { get; set; }
    public int height_px { get; set; }
    public double tl_lat { get; set; }
    public double tl_lon { get; set; }
    public double tr_lat { get; set; }
    public double tr_lon { get; set; }
    public double bl_lat { get; set; }
    public double bl_lon { get; set; }
    public string etag { get; set; } = null!;
    public byte[] image { get; set; } = null!;
  }
}
