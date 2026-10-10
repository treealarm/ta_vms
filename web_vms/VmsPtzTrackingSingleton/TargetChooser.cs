using Vms.Analytics;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Which of the boxes in view a camera should follow, given what it was told to prefer.
/// </summary>
/// <remarks>
/// Shared by both aiming laws. A camera guiding itself and a slave being pointed at a master's
/// objects disagree about almost everything -- one closes a loop on its own picture, the other runs
/// open loop through a survey -- but "which object" is the same question with the same answer, and
/// it is entirely a property of the boxes and the policy. Keeping one copy is what stops the two
/// from drifting into preferring different objects out of the same picture.
/// </remarks>
public static class TargetChooser
{
  // Detections whose bounding box covers more than this fraction of the frame area are almost
  // certainly either a false positive (camera too close, texture mistaken for silhouette) or an
  // object so close that the camera does not need to move at all. Either way they must not drive
  // the motor — an oversized box is not a useful tracking signal.
  public const float MaxBoxArea = 0.25f;

  /// <summary>
  /// The detection whose box actually touches the ground, for the object this one belongs to.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Aiming a slave reads the bottom edge of a box as the place the object meets the ground -- that
  /// is the assumption the whole binding rests on, and the reason the survey is marked on the
  /// ground. A face box does not meet the ground: its bottom edge is a chin. Feed one to the
  /// mapping and the slave is sent to wherever the ground is *behind* the person, metres away; and
  /// when the detector alternates between reporting the face and the whole person, as it does, the
  /// aim point jumps between chin and feet and the camera swings back and forth between zones. No
  /// dead band helps, because the jump is real and large.
  /// </para>
  /// <para>
  /// The way out is in the data: the CV detector gives a person and the face found on that person
  /// the same track id, differing only by kind (see TargetKey). So a face can be traded for the
  /// body it belongs to whenever the body is also in the batch. When it is not, there is no honest
  /// ground point to be had -- estimating feet from a chin is guessing at the subject's height --
  /// and the caller aims at nothing rather than somewhere wrong.
  /// </para>
  /// <para>
  /// This does not apply to a camera guiding itself. That one is framing its own picture, where a
  /// face is the better thing to centre on and nothing is being projected onto a ground plane.
  /// </para>
  /// </remarks>
  /// <returns>The same sample, its body, or null when no ground contact is available.</returns>
  public static DetectionSample? GroundContact(
    DetectionSample sample, IReadOnlyList<DetectionSample> batch)
  {
    if (StandsOnGround(sample.Kind))
      return sample;

    foreach (var candidate in batch)
    {
      if (candidate.Source == sample.Source
        && candidate.TrackId == sample.TrackId
        && StandsOnGround(candidate.Kind))
        return candidate;
    }
    return null;
  }

  /// <summary>Whether a box of this kind has its bottom edge on the ground.</summary>
  /// <remarks>
  /// A person, a vehicle and an animal stand on it. A face and a licence plate are parts of one of
  /// those, framed in mid-air.
  /// </remarks>
  public static bool StandsOnGround(DetectionKind kind) =>
    kind is DetectionKind.Person or DetectionKind.Vehicle or DetectionKind.Animal;

  /// <summary>The boxes that may drive a motor at all: right producer, plausible size.</summary>
  public static List<DetectionSample> Usable(
    IEnumerable<DetectionSample> batch, TrackingPolicy policy) =>
    batch.Where(s => s.BoxArea <= MaxBoxArea && policy.Accepts(s.Source)).ToList();

  /// <summary>
  /// Which object to follow when the camera has none.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The kinds are a strict order of preference, not a filter with a tie-break: the first kind on
  /// the list that is present in the picture wins outright, so a camera told "vehicle then person"
  /// ignores a person for as long as any vehicle is in view. An empty list means the camera was
  /// told nothing and takes whatever it can see.
  /// </para>
  /// <para>
  /// Among equals it takes the one nearest the centre of frame. That is the one already closest to
  /// being framed, so the camera starts by moving the least; and because it is a property of the
  /// picture rather than of the detector, the same object keeps winning from one sample to the next
  /// instead of the choice flickering between two of them. Picking the largest box was the
  /// alternative and behaves worse: apparent size changes as things approach, so the camera hands
  /// itself back and forth between objects at different distances.
  /// </para>
  /// </remarks>
  public static DetectionSample? Choose(List<DetectionSample> candidates, TrackingPolicy policy)
  {
    if (candidates.Count == 0)
      return null;

    if (policy.Kinds.Count == 0)
      return Nearest(candidates);

    foreach (var kind in policy.Kinds)
    {
      var ofKind = candidates.Where(s => s.Kind == kind).ToList();
      if (ofKind.Count > 0)
        return Nearest(ofKind);
    }

    // Nothing preferred is in view. The camera stays where it is rather than following something
    // it was told not to care about.
    return null;
  }

  public static DetectionSample? Nearest(List<DetectionSample> candidates)
  {
    DetectionSample? best = null;
    var bestDistance = float.MaxValue;
    foreach (var sample in candidates)
    {
      var dx = sample.CenterX - 0.5f;
      var dy = sample.CenterY - 0.5f;
      var distance = (dx * dx) + (dy * dy);
      if (distance >= bestDistance)
        continue;
      bestDistance = distance;
      best = sample;
    }
    return best;
  }
}
