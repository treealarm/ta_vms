namespace web_vms.Types.Dto;

public record RecordingProfileDto
{
  public string Id { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public string Mode { get; init; } = "off";
  public uint PreRecordSeconds { get; init; }
  public uint PostRecordSeconds { get; init; }
  public uint SegmentSeconds { get; init; }
  public bool IsOverridePreset { get; init; }
  public bool IsBuiltin { get; init; }
}

public record CameraStreamDto
{
  public required string StreamName { get; init; }
  public string ProfileToken { get; init; } = string.Empty;
  public string Path { get; init; } = string.Empty;
  public bool WebRtc { get; init; }
  public RecordingProfileDto? RecordingProfile { get; init; }
  public RecordingProfileDto? OverrideProfile { get; init; }
  public bool? Disabled { get; init; }
  /// <summary>Relative URL to our WHEP gateway (proxies to MediaMTX). Filled by web_vms.Server when WebRtc=true.</summary>
  public string? WhepUrl { get; init; }
}

public record StreamOverrideDto
{
  public required string CameraId { get; init; }
  public required string StreamName { get; init; }
  public required string ProfileId { get; init; }
}

public record MediaMtxServerDto
{
  public string Id         { get; init; } = string.Empty;
  public string Name       { get; init; } = string.Empty;
  public string Host       { get; init; } = string.Empty;
  public string WebrtcHost { get; init; } = string.Empty;
  public int    RtspPort   { get; init; } = 8554;
  public int    WebrtcPort { get; init; } = 8889;
  public int    ApiPort    { get; init; } = 9997;
}

public record CameraSetupDto
{
  public required string CameraId { get; init; }
  public string Name { get; init; } = string.Empty;
  public string? ServerId { get; init; }
  public bool Recording { get; init; }
  /// <summary>Derived from first stream's profile mode; used by media server control.</summary>
  public string RecordingMode { get; init; } = "off";
  public bool WebRtc { get; init; }
  /// <summary>Kept for API backward compat; maps to first stream's Path.</summary>
  public string? Path { get; init; }
  public CredentialsDto? Cred { get; init; }
  public OnvifConnectionDto? OnvifConn { get; set; }
  /// <summary>Filled by web_vms.Server when merging FS retention status.</summary>
  public bool? ActiveRetentionWindow { get; init; }
  /// <summary>An active retention window with no specific stream retains every stream.</summary>
  public bool? AllStreamsRetained { get; init; }
  /// <summary>Specific stream_names currently retained (an alarm scoped to those streams).</summary>
  public IReadOnlyList<string>? RetainedStreams { get; init; }
  public IReadOnlyList<CameraStreamDto> Streams { get; init; } = [];
  /// <summary>Assigned MediaMTX server ID (uuid).</summary>
  public string? MediaMtxServerId { get; init; }
  public bool OnvifEventsEnabled { get; init; }
  public bool HasOnvifFilters    { get; init; }
  public bool OnvifManaged       { get; init; }
  public bool? Disabled          { get; init; }
  /// <summary>True when the camera has an enabled analytics watch; filled by web_vms.Server when
  /// listing cameras. Drives whether the live-video bbox overlay toggle is offered.</summary>
  public bool HasAnalytics       { get; init; }
  /// <summary>Recording schedules assigned to this camera (uuids).</summary>
  public IReadOnlyList<string> ScheduleIds { get; init; } = [];
  /// <summary>True while one of the assigned schedule windows is open. Recomputed by VmsCfg on
  /// every read, never stored. While true the camera records continuously, overriding both the
  /// stream profile and any manual or alarm override.</summary>
  public bool ScheduleActive     { get; init; }
}
