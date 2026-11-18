using System.Collections.Generic;

namespace VmsMap.Domain
{
  // The figures a box read returned, and whether the read stopped before the box ran out. The
  // answer is ordered by id, so a truncated one is a stable prefix of the box — the caller can
  // tell "the canvas is partial" from "the canvas holds this much".
  public sealed record GeoResultDTO(Dictionary<string, GeoObjectDTO> Objects, bool Truncated);
}
