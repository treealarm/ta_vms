namespace VmsMap.Domain
{
  // The tree node of a camera: one id per camera, the same across restarts and replicas. The map
  // derives it instead of storing it, so the adapter and the placement editor create the same node
  // without a lookup table, and a camera's states and rights stay on it wherever it is placed.
  //
  // A camera is a device, so the derivation is DeviceObjectId's; this is only the camera's spelling
  // of it. Keeping one derivation is what stops a camera from having two nodes.
  public static class CameraObjectId
  {
    public static string For(string cameraId) => DeviceObjectId.ForCamera(cameraId);
  }
}
