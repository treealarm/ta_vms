using Vms.Analytics;

namespace VmsPtzTrackingSingleton;

/// <summary>One box on the live overlay, named the way the overlay names it.</summary>
/// <remarks>
/// All three parts are load-bearing. The CV detector and a camera's own Scene Description number
/// their tracks independently, so two producers on one camera would collide without
/// <paramref name="Source"/>; and a person and the face found on that person share a track id,
/// differing only by kind.
/// </remarks>
public sealed record TargetKey(string Source, long TrackId, DetectionKind Kind);

/// <summary>One sighting, reduced to what the control law needs.</summary>
/// <param name="Left">Left edge of the box, 0..1 from the left of the frame.</param>
/// <param name="Top">Top edge of the box, 0..1 from the top of the frame.</param>
/// <param name="Width">Box width as a fraction of the frame.</param>
/// <param name="Height">Box height as a fraction of the frame.</param>
/// <param name="ObjectId">
/// Re-identification label, when re-id has resolved this track. The strongest reacquire signal
/// there is: it survives the track id changing, which is exactly what happens while the camera
/// moves and the whole scene shifts under the detector.
/// </param>
/// <remarks>
/// The whole rectangle is kept rather than just its centre because two decisions need the edges
/// and not the middle: whether the object is entirely inside the frame (a camera whose subject is
/// fully visible has little to gain by fidgeting), and how large the box is.
/// </remarks>
public sealed record DetectionSample(
  string Source,
  long TrackId,
  DetectionKind Kind,
  float Left,
  float Top,
  float Width,
  float Height,
  string? ObjectId,
  DateTime DetectedAt)
{
  public TargetKey Key => new(Source, TrackId, Kind);
  public float CenterX => Left + (Width / 2f);
  public float CenterY => Top + (Height / 2f);
  /// Normalised bbox area (0..1). A large box is likely a false positive, or an object so close
  /// the camera cannot usefully pan to it.
  public float BoxArea => Width * Height;
}

/// <summary>What a camera's self-guidance is doing, for the operator's monitor.</summary>
/// <param name="Active">
/// False for a camera configured to guide itself that has no control loop — no VmsOnvif can reach
/// it, or its telemetry flag is clear. The monitor needs these listed rather than omitted: its
/// button reflects the camera's setting, and a camera missing from this list altogether would read
/// as switched off and could not be switched off.
/// </param>
/// <param name="MasterCameraId">
/// Whose picture this camera is following, when it is not its own. What makes this worth carrying
/// to the monitor is the other direction: it is how a card works out that it is somebody's master,
/// and so that clicking a box on it will point a camera somewhere else.
/// </param>
public sealed record TrackingSnapshot(
  string CameraId,
  bool Active,
  TargetKey? Target,
  bool ExplicitTarget,
  DateTime? TargetSeenAt,
  string LastError,
  string? MasterCameraId = null);
