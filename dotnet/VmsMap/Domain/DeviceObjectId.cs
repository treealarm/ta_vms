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
  }
}
