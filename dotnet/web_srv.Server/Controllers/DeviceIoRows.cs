using Vms.Onvif;

namespace web_srv;

/// <summary>
/// The rows the device I/O endpoints serve. They live here rather than inline in the controller so
/// a contract test can pin the wire names the monitor tile and the device panel read: the lists
/// moved behind VmsOnvif's presence cache, and the JSON did not change.
/// </summary>
public static class DeviceIoRows
{
  /// <summary>What the map knows about one part: its rule, and what the rule resolves to today.</summary>
  public readonly record struct InputSettings(
    string? ArmMode,
    bool? AlarmEnabled,
    bool? Armed,
    string? ArmingSource,
    bool Bypassed,
    DateTimeOffset? BypassUntil,
    bool CanControl);

  public static object Relay(RelayOutput relay, bool canControl) => new
  {
    token   = relay.Token,
    mode    = FormatMode(relay.Mode),
    idleState = FormatIdleState(relay.IdleState),
    delayMs = relay.DelayMs,
    // null until the camera has said anything: "no event yet" is not "inactive". While a command
    // waits for the camera's confirmation this is the state that was asked for.
    liveActive = relay.HasLiveActive ? relay.LiveActive : (bool?)null,
    // The value above is the operator's wish, not the camera's report yet.
    livePending = relay.Pending,
    canControl,
  };

  public static object Input(DigitalInput input, InputSettings settings) => new
  {
    token = input.Token,
    // A camera that did not report an idle state gets null, not the default word: the polarity is
    // unknown, not "closed".
    idleState = input.HasIdleState ? FormatIdleState(input.IdleState) : (string?)null,
    // Logical state from the camera's property events; a digital input reports true/false where a
    // relay reports active/inactive. null means the camera has not reported one yet.
    liveActive = input.HasLiveActive ? input.LiveActive : (bool?)null,
    // The configured mode, and what it actually resolves to: 'inherit' is not "disarmed" — the
    // camera, or the zone the camera stands in, may have the sensor on guard. null when the part
    // has no row, or no node in the map yet.
    armMode = settings.ArmMode,
    alarmEnabled = settings.AlarmEnabled,
    armed = settings.Armed,
    armingSource = settings.ArmingSource,
    bypassed = settings.Bypassed,
    bypassUntil = settings.BypassUntil,
    canControl = settings.CanControl,
  };

  public static string FormatMode(RelayMode mode) =>
    mode == RelayMode.Bistable ? "bistable" : "monostable";

  public static string FormatIdleState(IdleState idleState) =>
    idleState == IdleState.Open ? "open" : "closed";
}
