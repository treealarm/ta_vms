using System;

namespace VmsMap.Db
{
  public record DBDiagram : BasePgEntity
  {
    // The diagram type's id: a rename must not orphan the diagrams of a type.
    public Guid? dgr_type { get; set; }
    // The diagram's own canvas; a region of the parent's type says where it stands, not what it
    // opens on.
    public DBDiagramCoord? geometry { get; set; }
    public string? region_id { get; set; }
    // A row of `files`, not a path on a volume.
    public Guid? file_id { get; set; }
  }
}
