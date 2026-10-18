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

  /// <summary>Sends the camera to a position, rather than nudging it in a direction.</summary>
  /// <remarks>
  /// How a slave is aimed: it usually cannot see what it is being pointed at until it arrives, so
  /// there is nothing to close a loop on and the position has to be commanded outright.
  /// <para>
  /// The values are in whatever space the camera answers in, passed through untouched. The binding
  /// was fitted to positions read back from this same camera, so its output is already in the
  /// camera's units whether those are normalised or degrees.
  /// </para>
  /// </remarks>
  /// <param name="zoom">
  /// Absolute, always sent: an absolute move carries a zoom whether or not one is wanted, so leaving
  /// it out would still move the lens. 0 is the wide end.
  /// </param>
  Task<string?> AimAsync(string cameraId, float pan, float tilt, float zoom, CancellationToken ct);

  /// <summary>Drives the camera to one of its stored presets.</summary>
  /// <remarks>
  /// The coarser way to aim a slave, and on some cameras the only one: firmware that accepts
  /// AbsoluteMove and quietly ignores it will still honour a preset.
  /// </remarks>
  Task<string?> GotoPresetAsync(string cameraId, string presetToken, CancellationToken ct);
}
