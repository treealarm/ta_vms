
using System.Collections.Generic;

namespace VmsMap.Domain
{
  public record FigureZoomedDTO: BaseMarkerDTO, IObjectProps
  {
    public string? zoom_level { get; set; }

    // Optional property to update/receive properties.
    public List<ObjExtraPropertyDTO>? extra_props { get; set; }
  }
}
