namespace web_vms.Types.Recording;

/// <summary>
/// A recording mode, spelled once.
///
/// Each service already carries this concept in its own shape: VmsFs and media_server use the
/// generated <c>Vms.Cfg.RecordingMode</c> enum, the REST DTOs use the strings "continuous" /
/// "prealarm" / "off". The two generated enums are distinct CLR types in distinct assemblies —
/// VmsFs and VmsCfg each compile vms_cfg.proto for themselves — so neither of them can be the
/// common currency for a shared rule. This one can.
/// </summary>
public enum RecordingModeKind
{
  Off,
  Continuous,
  Prealarm,
}

/// <summary>
/// The precedence rule that decides what a stream records. It used to be written out at every
/// place that needed it, in both languages, and the copies had already drifted apart; a rule that
/// governs whether footage reaches the archive is not one to state six times.
/// </summary>
public static class RecordingRules
{
  /// <summary>
  /// What this stream actually records, in precedence order:
  /// <list type="number">
  ///   <item>A stream that is not available for recording never records — ahead of everything
  ///   else, including the schedule. Its configured profile is deliberately left untouched in the
  ///   database, so restoring the flag resumes recording with the profile already chosen.</item>
  ///   <item>An open recording schedule window forces continuous recording on every stream of the
  ///   camera, ahead of the profile and of any manual or alarm override.</item>
  ///   <item>An override profile, when one is set — its mode wins even when that mode is off.</item>
  ///   <item>The base profile; nothing configured at all means off.</item>
  /// </list>
  /// Pass <paramref name="overrideMode"/> as null when there is no override profile, not when the
  /// override itself says off: those two mean different things at step 3.
  /// </summary>
  public static RecordingModeKind Effective(
    bool scheduleActive,
    bool recordable,
    RecordingModeKind? overrideMode,
    RecordingModeKind? baseMode)
  {
    if (!recordable)
      return RecordingModeKind.Off;

    if (scheduleActive)
      return RecordingModeKind.Continuous;

    return overrideMode ?? baseMode ?? RecordingModeKind.Off;
  }

  /// <summary>
  /// Reads the spelling the REST DTOs use. Anything unrecognised is off: an unknown mode must not
  /// be able to start a recording.
  /// </summary>
  public static RecordingModeKind Parse(string? mode) => mode switch
  {
    "continuous" => RecordingModeKind.Continuous,
    "prealarm"   => RecordingModeKind.Prealarm,
    _            => RecordingModeKind.Off,
  };

  /// <summary>The spelling the REST DTOs use.</summary>
  public static string ToWire(RecordingModeKind mode) => mode switch
  {
    RecordingModeKind.Continuous => "continuous",
    RecordingModeKind.Prealarm   => "prealarm",
    _                            => "off",
  };
}
