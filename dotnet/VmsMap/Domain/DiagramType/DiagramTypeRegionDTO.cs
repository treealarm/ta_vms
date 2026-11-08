using System.Collections.Generic;

namespace VmsMap.Domain
{
    public record DiagramTypeRegionDTO
    {
        public string? region_key { get; set; }
        public DiagramCoordDTO? geometry { get; set; }
        public Dictionary<string, string>? styles { get; set; } // Extra styling the editor keeps with the region.
        // The device part this region stands for, or null for a plain drawing or caption.
        public DiagramTypePartDTO? part { get; set; }
    }

    public record DiagramTypePartDTO
    {
        public string? part_kind { get; set; }
        public string? match_by { get; set; }
        public string? match_value { get; set; }
    }
}
