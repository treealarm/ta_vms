namespace VmsMap.Db
{
  // Where a landmark stands on the ground in a fixed camera's picture, normalised 0..1 across the
  // frame, like a detection box. The image-to-ground fit is made to these marks and never stored.
  public record DBGroundObservation
  {
    public string camera_id { get; set; } = null!;
    public System.Guid landmark_id { get; set; }
    public float x { get; set; }
    public float y { get; set; }
  }
}
