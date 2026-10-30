namespace VmsMap.Clients
{
  // What the map needs to know about a camera: that it exists and how to name its tree node.
  // Cameras live in VmsCfg's database, so a placement write asks there instead of relying on a
  // foreign key that cannot span the two.
  public sealed record CameraInfo(string CameraId, string Name);

  public interface ICameraCatalog
  {
    /// <summary>Null when VmsCfg does not know the camera.</summary>
    Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct);
  }
}
