using System;
using System.Collections.Generic;

namespace VmsMap.Db
{
  public record DBDiagramTypeRegion
  {
    public Guid diagram_type_id { get; set; }
    public DBDiagramType diagram_type { get; set; } = null!;
    public string region_key { get; set; } = null!;
    public DBDiagramCoord geometry { get; set; } = null!;
    public Dictionary<string, string>? styles { get; set; }
  }

  public record DBDiagramType : BasePgEntity
  {
    public string name { get; set; } = null!;
    // Background image: a row of `files`.
    public Guid? file_id { get; set; }
    public List<DBDiagramTypeRegion> regions { get; set; } = new();
  }
}
