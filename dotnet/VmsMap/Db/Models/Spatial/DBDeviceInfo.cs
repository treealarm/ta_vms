namespace VmsMap.Db
{
  // What a device says about itself: manufacturer, model, firmware. A snapshot of the hardware,
  // owned by the integration that reads it, stored so a diagram template keeps matching after a
  // restart without waiting for the next sweep.
  //
  // A row is written only when the device answered. An empty string is "it answered and said
  // nothing", which is a different fact from "it was not asked" — the latter leaves the row alone.
  public record DBDeviceInfo
  {
    public string source_kind { get; set; } = null!;
    public string source_id { get; set; } = null!;
    public string manufacturer { get; set; } = "";
    public string model { get; set; } = "";
    public string firmware { get; set; } = "";
  }
}
