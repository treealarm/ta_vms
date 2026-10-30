
using System.Collections.Generic;

namespace VmsMap.Domain
{
    public record DiagramTypeRegionDTO
    {
        public string? region_key { get; set; }
        public DiagramCoordDTO? geometry { get; set; }
        public Dictionary<string, string>? styles { get; set; } // Extra styling the editor keeps with the region.
  }
}
