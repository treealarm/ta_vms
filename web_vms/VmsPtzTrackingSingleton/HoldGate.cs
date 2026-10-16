namespace VmsPtzTrackingSingleton;

/// <summary>Keeps a camera's automatic aiming off its motor for a while, so a person can drive it.</summary>
/// <remarks>
/// <para>
/// One per camera, owned by <see cref="TrackingSessionManager"/> and handed to whichever session
/// drives that camera -- self-guided or a slave, now or started later. A calibration wizard holds
/// the camera from the moment it opens, and a session that a reconcile starts meanwhile must not
/// take the camera back from it; a hold kept inside one session would be lost with that session.
/// </para>
/// <para>
/// It expires rather than being switched off, and whoever holds renews it. A held camera is a camera
/// not doing its job, and a browser tab that crashed or a laptop that was closed must not be able to
/// leave one held forever.
/// </para>
/// </remarks>
public sealed class HoldGate
{
  /// <summary>What the operator's monitor shows for a held camera: neither a fault nor silence.</summary>
  public const string Reason = "Held by an operator who is calibrating or aiming it by hand";

  private readonly Lock _gate = new();
  private DateTime _until = DateTime.MinValue;

  public bool IsHeld => DateTime.UtcNow < Until;

  public DateTime Until
  {
    get
    {
      lock (_gate)
        return _until;
    }
  }

  /// <summary>Holds the camera until at least <paramref name="until"/>; a longer hold already placed stays.</summary>
  /// <remarks>
  /// Never taken back early except by <see cref="Release"/>. An aim that failed leaves its hold to
  /// run out rather than undoing it: undoing it exactly would mean remembering every renewal that
  /// arrived meanwhile, and a camera the aim could not reach gains nothing from being released.
  /// </remarks>
  public void Extend(DateTime until)
  {
    lock (_gate)
    {
      if (until > _until)
        _until = until;
    }
  }

  /// <summary>Hands the camera back now, whoever held it.</summary>
  public void Release()
  {
    lock (_gate)
      _until = DateTime.MinValue;
  }
}
