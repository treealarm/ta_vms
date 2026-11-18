namespace VmsOnvifActors;

/// <summary>
/// A short window in which a camera whose ONVIF services could not be resolved is answered for
/// without touching it again.
/// </summary>
/// <remarks>
/// A camera that is switched off costs one service discovery (several attempts, seconds) per call;
/// a caller polling its PTZ status once a second therefore kept a whole discovery running and wrote
/// an error line for every attempt. The window collapses those calls into one attempt per window,
/// and it is deliberately short: a camera that comes back has to start working again without an
/// operator waiting for it.
/// </remarks>
public sealed class CameraUnavailableBackoff(TimeSpan window, TimeProvider time)
{
  private string? _cameraKey;
  private DateTimeOffset _retryAt;

  /// <summary>Whether <paramref name="cameraKey"/> is still inside the window it entered.</summary>
  /// <remarks>
  /// Strictly before <c>_retryAt</c>: at the moment the window ends the camera is asked again.
  /// </remarks>
  public bool IsBackedOff(string cameraKey) =>
    cameraKey == _cameraKey && time.GetUtcNow() < _retryAt;

  /// <summary>Starts the window for this camera, from now.</summary>
  public void Note(string cameraKey)
  {
    _cameraKey = cameraKey;
    _retryAt = time.GetUtcNow() + window;
  }
}
