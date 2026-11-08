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

    // A template for one kind of real device, or null for an ordinary mnemoscheme. The model is
    // matched exactly and case-insensitively; an empty string is "any". Auto-attach, not the
    // presence of a model, is what lets the service create the diagram by itself.
    public string? source_kind { get; set; }
    public string device_manufacturer { get; set; } = "";
    public string device_model { get; set; } = "";
    public bool auto_attach { get; set; }

    public List<DBDiagramTypeRegion> regions { get; set; } = new();
    public List<DBDiagramTypePart> parts { get; set; } = new();
  }
}
