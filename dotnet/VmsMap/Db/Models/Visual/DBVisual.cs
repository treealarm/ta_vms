using System;

namespace VmsMap.Db
{
  // How a node is drawn, separated from its abstract properties: the fields Square kept as
  // magic `__color`, `__image`, `__image_width`, `__image_height`, `__image_rotate` and `__text`
  // properties. One row per node, its key is the node.
  public record DBVisual
  {
    public Guid object_id { get; set; }
    public string? color { get; set; }
    // Icons are small enough to live here; plan pictures and diagram backgrounds are `files`.
    public byte[]? image { get; set; }
    public int? image_width { get; set; }
    public int? image_height { get; set; }
    public double? rotation_deg { get; set; }
    public string? text { get; set; }
  }
}
