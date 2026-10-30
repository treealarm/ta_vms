using System;

namespace VmsMap.Db
{
  public record DBDiagram : BasePgEntity
  {
    // The diagram type's id: a rename must not orphan the diagrams of a type.
    public Guid? dgr_type { get; set; }
    public DBDiagramCoord geometry { get; set; } = null!;
    public string? region_id { get; set; }
    // A row of `files`, not a path on a volume.
    public Guid? file_id { get; set; }
  }
}
