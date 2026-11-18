using System.Collections.Generic;

namespace VmsMap.Domain
{
  // The figures a box read returned, whether the read stopped before the box ran out, and the id
  // it stopped at. The answer is ordered by id, so a truncated one is a stable prefix of the box —
  // the caller can tell "the canvas is partial" from "the canvas holds this much", and can ask for
  // the next page with NextCursor. The cursor is the read's bookmark, not the last figure of the
  // answer: rights may hide figures up to it, and the next page starts after it all the same.
  public sealed record GeoResultDTO(
    Dictionary<string, GeoObjectDTO> Objects, bool Truncated, string? NextCursor);
}
