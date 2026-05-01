namespace web_vms.Types.Dto;

public record PreAlarmRecordingSettingsDto
{
  public uint PreRecordSeconds { get; init; }
  public uint PostRecordSeconds { get; init; }
  public uint SegmentSeconds { get; init; }
}
