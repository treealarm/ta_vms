using web_vms.Types.Recording;
using ProtoRecordingMode = Vms.Cfg.RecordingMode;

namespace web_vms;

/// <summary>
/// Bridges <see cref="RecordingModeKind"/> — the shared rule's currency — and the generated
/// protobuf enum this assembly compiles. It lives here rather than in web_vms.Types because the
/// generated type is per-assembly: VmsFs compiles vms_cfg.proto for itself and gets a different
/// CLR type of the same name.
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
