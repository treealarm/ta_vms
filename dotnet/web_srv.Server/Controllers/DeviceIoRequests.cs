using MapProto = Vms.Map;

namespace web_srv;

/// <summary>
/// A camera part's settings are two writes in two services: its name is VmsCfg's row, and its
/// arming and alarm rule are the map's. The split is explicit here so that renaming a part cannot
/// carry a stale copy of its behaviour over a concurrent change.
/// </summary>
public static class DeviceIoRequests
{
  // The kind a camera's parts are registered under in VmsMap's device_node registry. A fire panel
  // or another device hub is a kind of its own there, never a second spelling of this one.
  public const string CameraSource = "camera";
  public const string DigitalInputKind = "digital_input";

  /// <summary>The name write, or null when the request does not carry one.</summary>
  public static Vms.Cfg.SetDeviceSettingsRequest? NameRequest(
    string cameraId, DeviceSettingsBody body) =>
    body.Name is null
      ? null
      : new Vms.Cfg.SetDeviceSettingsRequest
      {
        Settings = new Vms.Cfg.DeviceSettings
        {
          CameraId = cameraId,
          Kind = body.Kind,
          Token = body.Token,
          Name = body.Name,
        },
      };

  /// <summary>The behaviour write for a digital input; a relay carries no arming or alarm rule.</summary>
  public static MapProto.SetDeviceSettingsRequest? BehaviourRequest(
    string cameraId, DeviceSettingsBody body) =>
    body.Kind == DigitalInputKind
      ? new MapProto.SetDeviceSettingsRequest
      {
        Key = PartKey(cameraId, DigitalInputKind, body.Token),
        ArmMode = body.ArmMode ?? string.Empty,
        TriggerOn = body.TriggerOn ?? string.Empty,
        AlarmEnabled = body.AlarmEnabled,
      }
      : null;

  public static MapProto.DeviceSettingsKey PartKey(string cameraId, string kind, string token) => new()
  {
    SourceKind = CameraSource,
    SourceId = cameraId,
    PartKind = kind,
    PartToken = token,
  };
}
