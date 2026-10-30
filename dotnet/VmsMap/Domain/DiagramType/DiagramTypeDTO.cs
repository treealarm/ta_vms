using System.Collections.Generic;

namespace VmsMap.Domain
{
  public record DiagramTypeDTO
  {
    public string? id { get; set; }
    public string? name { get; set; }
    public string? file_id { get; set; }
    public List<DiagramTypeRegionDTO>? regions { get; set; }
  }
}
