namespace VmsMap.Domain
{
  /// <summary>
  /// The tree node id of a device, or of one part of it. One derivation for the whole registry:
  /// a camera's own node, its digital inputs and its relays all come from here, so a device can
  /// never end up with two nodes that disagree about which key they stand for.
  /// </summary>
  public static class DeviceObjectId
  {
    private const string Version = "device_node";

    /// <summary>The device type whose parts are rooted at one camera row in VmsCfg.</summary>
    public const string CameraSource = "camera";

    /// <summary>The whole device, as opposed to one of its parts.</summary>
    public const string DevicePart = "device";

    public const string DigitalInputPart = "digital_input";
    public const string RelayPart = "relay";

    /// <summary>
    /// The node id of `sourceKind/sourceId`'s `partKind` part. `partToken` is the part's own token,
    /// empty for the device as a whole.
    /// </summary>
    public static string For(string sourceKind, string sourceId, string partKind, string partToken) =>
      ObjectIdGenerator.GenerateObjectId($"{sourceKind}|{sourceId}|{partKind}|{partToken}", Version);

    /// <summary>The node id of a camera as a device.</summary>
    public static string ForCamera(string cameraId) =>
      For(CameraSource, cameraId, DevicePart, string.Empty);

    /// <summary>The part kind a device's diagram is registered under, as one node of its own.</summary>
    public const string DiagramPart = "diagram";

    // Its own derivation: a diagram is not a part of the device's I/O, and the type it is drawn
    // from is deliberately absent — a device that starts reporting another model keeps its node.
    private const string DiagramVersion = "device_diagram";

    /// <summary>The node id of one device's diagram in `slot` — a rear panel today, a front one later.</summary>
    public static string ForDeviceDiagram(string sourceKind, string sourceId, string slot) =>
      ObjectIdGenerator.GenerateObjectId(
        $"{sourceKind}|{sourceId}|{DiagramPart}|{slot}", DiagramVersion);
  }
}
