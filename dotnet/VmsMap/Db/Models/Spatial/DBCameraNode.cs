using System;

namespace VmsMap.Db
{
  // The index from a camera's node to the camera it stands for. Only the canonical node has a row:
  // pictures of the camera are replicas and are named by their placement row.
  public record DBCameraNode
  {
    public Guid object_id { get; set; }
    public string camera_id { get; set; } = null!;
  }
}
