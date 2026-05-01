namespace web_vms.Types.Dto;

public record CameraStateDto
{
  public required string CameraId { get; init; }
  public string? ServerId { get; init; }
  public bool Recording { get; init; }
  /// <summary>off | continuous | prealarm — persisted in cfg; drives Recording when applied.</summary>
  public string RecordingMode { get; init; } = "off";
  public bool WebRtc { get; init; }
  public string? RtspUrl { get; init; }
  public CredentialsDto? Cred { get; init; }
  public OnvifConnectionDto? OnvifConn { get; set; }
  public PreAlarmRecordingSettingsDto? PreAlarm { get; init; }
  /// <summary>Filled by web_vms.Server when merging FS retention status.</summary>
  public bool? ActiveRetentionWindow { get; init; }
}
