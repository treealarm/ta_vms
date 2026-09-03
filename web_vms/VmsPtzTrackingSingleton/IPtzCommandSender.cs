namespace VmsPtzTrackingSingleton;

/// <summary>
/// The only thing a <see cref="TrackingSession"/> is allowed to do to the world.
/// </summary>
/// <remarks>
/// An interface so the control law can be exercised without a camera, and so the session stays free
/// of ambient state — everything it needs arrives through its constructor. That is also what would
/// let it move into a Dapr actor unchanged, should the number of simultaneous tracks ever outgrow
/// one process (see the plan's note on why it is not one today).
/// </remarks>
public interface IPtzCommandSender
{
  /// <param name="pan">-1..1, positive is right.</param>
  /// <param name="tilt">-1..1, positive is up.</param>
  /// <returns>Null on success, otherwise the error to show the operator.</returns>
  Task<string?> MoveAsync(string cameraId, float pan, float tilt, CancellationToken ct);

  Task<string?> StopAsync(string cameraId, CancellationToken ct);
}
