namespace web_vms.Types.Dto;

public record RecordingProfileDto
{
  public string Id { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public string Mode { get; init; } = "off";
  public uint PreRecordSeconds { get; init; }
  public uint PostRecordSeconds { get; init; }
  public uint SegmentSeconds { get; init; }
}

public record CameraStreamDto
{
  public required string StreamName { get; init; }
  public string ProfileToken { get; init; } = string.Empty;
  public string RtspUrl { get; init; } = string.Empty;
  public bool WebRtc { get; init; }
  public RecordingProfileDto? RecordingProfile { get; init; }
}

public record CameraSetupDto
{
  public required string CameraId { get; init; }
  public string? ServerId { get; init; }
  public bool Recording { get; init; }
  /// <summary>Derived from first stream's profile mode; used by media server control.</summary>
  public string RecordingMode { get; init; } = "off";
  public bool WebRtc { get; init; }
  /// <summary>Kept for API backward compat; maps to first stream's RtspUrl.</summary>
  public string? RtspUrl { get; init; }
  public CredentialsDto? Cred { get; init; }
  public OnvifConnectionDto? OnvifConn { get; set; }
  /// <summary>Filled by web_vms.Server when merging FS retention status.</summary>
  public bool? ActiveRetentionWindow { get; init; }
  public IReadOnlyList<CameraStreamDto> Streams { get; init; } = [];
}
