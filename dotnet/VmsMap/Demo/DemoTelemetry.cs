namespace VmsMap.Demo
{
  /// <summary>
  /// The readings the demo shows for a camera that reports them: its place, how fast it moves, and
  /// the state of the relay and sensor wired around it. One definition for the diagram type, its
  /// regions and the feeder that fills them — a region whose name and feeding key could drift apart
  /// would be a value nobody sees.
  /// </summary>
  public static class DemoTelemetry
  {
    public const string TypeKey = "type/device-telemetry";

    public const string Lat = "lat";
    public const string Lon = "lon";
    public const string Speed = "speed";
    public const string Relay = "relay";
    public const string Sensor = "sensor";

    /// <summary>Every reading, in the order the region helper numbers its rows.</summary>
    public static readonly string[] Readings = [Lat, Lon, Speed, Relay, Sensor];
  }
}
