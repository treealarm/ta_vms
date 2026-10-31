using System.Collections.Generic;

namespace VmsMap.Domain
{
  // One node and the actions the caller effectively has on it. `camera_id` is set when the node
  // is a camera (or a picture of one), so the web can answer its per-camera checks without
  // deriving the node id again.
  public sealed record NodeRightsDTO
  {
    public string object_id { get; init; } = string.Empty;
    public string? camera_id { get; init; }
    public List<string> actions { get; init; } = [];
  }

  // The one switch that decides what a node with no rights rows means.
  public enum RightsMode
  {
    // No rows anywhere above the node: everyone may see it and use its actions.
    Open,
    // No rows: nobody may.
    Enforced,
  }
}
