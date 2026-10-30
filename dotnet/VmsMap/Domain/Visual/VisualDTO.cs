namespace VmsMap.Domain
{
  // How a node is drawn — the typed replacement for Square's magic `__color`, `__image`,
  // `__image_width`, `__image_height`, `__image_rotate` and `__text` properties.
  public record VisualDTO
  {
    public string? object_id { get; set; }
    public string? color { get; set; }
    // A small icon; plan pictures and diagram backgrounds are files.
    public byte[]? image { get; set; }
    public int? image_width { get; set; }
    public int? image_height { get; set; }
    // Degrees clockwise from north for a camera, or from upright for an icon.
    public double? rotation_deg { get; set; }
    public string? text { get; set; }
  }
}
