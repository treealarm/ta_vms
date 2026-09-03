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
/// <param name="CenterX">Box centre, 0..1 from the left edge.</param>
/// <param name="CenterY">Box centre, 0..1 from the top edge.</param>
/// <param name="BoxArea">Normalised bbox area (width × height, 0..1). Large boxes are likely
/// false positives or objects so close the camera cannot usefully pan to them.</param>
/// <param name="ObjectId">
/// Re-identification label, when re-id has resolved this track. The strongest reacquire signal
/// there is: it survives the track id changing, which is exactly what happens while the camera
/// moves and the whole scene shifts under the detector.
/// </param>
public sealed record DetectionSample(
  string Source,
  long TrackId,
  DetectionKind Kind,
  float CenterX,
  float CenterY,
  float BoxArea,
  string? ObjectId,
  DateTime DetectedAt)
{
  public TargetKey Key => new(Source, TrackId, Kind);
}

/// <summary>What a session is doing, for the operator's monitor.</summary>
public sealed record TrackingSnapshot(
  string CameraId,
  TargetKey? Target,
  bool ExplicitTarget,
  DateTime? TargetSeenAt,
  string LastError);
