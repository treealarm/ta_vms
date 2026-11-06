using System.Collections.Generic;

namespace VmsMap.Domain
{
  public record DiagramTypeDTO
  {
    public string? id { get; set; }
    public string? name { get; set; }
    public string? file_id { get; set; }
    public List<DiagramTypeRegionDTO>? regions { get; set; }

    // A template for one model of a real device; null for an ordinary mnemoscheme.
    public string? source_kind { get; set; }
    public string device_manufacturer { get; set; } = "";
    public string device_model { get; set; } = "";
    public bool auto_attach { get; set; }
  }
}
