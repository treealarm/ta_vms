using web_vms.Types.Recording;
using ProtoRecordingMode = Vms.Cfg.RecordingMode;

namespace VmsFs;

/// <summary>
/// Bridges <see cref="RecordingModeKind"/> — the shared rule's currency — and the generated
/// protobuf enum this assembly compiles. VmsFs compiles vms_cfg.proto for itself, so its
/// <c>Vms.Cfg.RecordingMode</c> is a different CLR type from web_vms.Server's one; that is why the
/// bridge cannot live in the shared project alongside the rule.
/// </summary>
public static class RecordingModeProto
{
  public static ProtoRecordingMode ToProto(RecordingModeKind mode) => mode switch
  {
    RecordingModeKind.Continuous => ProtoRecordingMode.RecordingContinuous,
    RecordingModeKind.Prealarm   => ProtoRecordingMode.RecordingPrealarm,
    _                            => ProtoRecordingMode.RecordingOff,
  };

  public static RecordingModeKind FromProto(ProtoRecordingMode mode) => mode switch
  {
    ProtoRecordingMode.RecordingContinuous => RecordingModeKind.Continuous,
    ProtoRecordingMode.RecordingPrealarm   => RecordingModeKind.Prealarm,
    _                                      => RecordingModeKind.Off,
  };
}
