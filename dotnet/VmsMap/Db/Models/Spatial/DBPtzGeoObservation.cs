namespace VmsMap.Db
{
  // Where a PTZ camera pointed when it was centred on a landmark, in the camera's own pan/tilt
  // units. The fit is made to these measurements and never stored.
  public record DBPtzGeoObservation
  {
    public string camera_id { get; set; } = null!;
    public System.Guid landmark_id { get; set; }
    public float pan { get; set; }
    public float tilt { get; set; }
    public float? zoom { get; set; }
  }
}
