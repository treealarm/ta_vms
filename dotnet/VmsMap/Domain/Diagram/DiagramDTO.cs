namespace VmsMap.Domain
{
  public record DiagramDTO
  {
    public string? id { get; set; }
    public DiagramCoordDTO? geometry { get; set; }
    public string? region_id { get; set; }
    public string? dgr_type { get; set; }
    public string? file_id { get; set; }
    // Where a representation stands in its parent; the geometry above is the paper it opens on,
    // read from the diagram it represents. Both are computed on the wire, never stored here.
    public DiagramCoordDTO? placement { get; set; }
    public bool is_representation { get; set; }
  }
}
