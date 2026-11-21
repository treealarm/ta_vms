using System;

namespace VmsMap.Db
{
  /// <summary>
  /// How a logical device object behaves: who decides its arming (`inherit` follows the zone it
  /// stands in) and, for a digital input, which contact state raises an alarm. The map owns this
  /// because it is the only consumer; VmsCfg keeps what services near the hardware read — the
  /// camera's effective flag, which the map derives and writes there.
  /// </summary>
  public class DBDeviceObjectSettings
  {
    public Guid object_id { get; set; }
    public string arm_mode { get; set; } = "inherit";
    public string? trigger_on { get; set; }
    public bool alarm_enabled { get; set; }
    /// <summary>The exclusion ends here; only `arm_mode = 'disarmed'` carries one.</summary>
    public DateTime? bypass_until { get; set; }
    /// <summary>The own mode to write back at the deadline: `inherit` or `armed`.</summary>
    public string? restore_mode { get; set; }
  }
}
