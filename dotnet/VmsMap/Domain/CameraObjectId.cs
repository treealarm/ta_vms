namespace VmsMap.Domain
{
  // The tree node of a camera: one id per camera, the same across restarts and replicas. The map
  // derives it instead of storing it, so the adapter and the placement editor create the same node
  // without a lookup table, and a camera's states and rights stay on it wherever it is placed.
  public static class CameraObjectId
  {
    private const string Version = "camera";

    public static string For(string cameraId) =>
      ObjectIdGenerator.GenerateObjectId(cameraId, Version);
  }
}
