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
  /// <summary>STUN server used by the browser for WHEP playback. Empty = no STUN (LAN only).</summary>
  public string StunUrl    { get; init; } = string.Empty;
  /// <summary>Media server node this MediaMTX serves. Empty = shared, usable by any node.</summary>
  public string ServerId   { get; init; } = string.Empty;
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
  /// <summary>
  /// Which stream to recover from the camera's own storage when the archive has a gap; empty
  /// disables it. On the camera rather than the stream: the card holds one archive, and which
  /// stream goes into it is chosen in the camera itself.
  /// </summary>
  public string EdgeBackfillStream { get; init; } = string.Empty;

  /// <summary>
  /// Ignore the object boxes this camera puts in its own RTSP metadata track. Not the same
  /// as switching analytics off on the device — that is the ONVIF metadata configuration,
  /// and it has its own control. This one is ours: the camera keeps sending, and the
  /// documents are dropped before they are parsed.
  /// </summary>
  public bool OnvifMetadataDisabled { get; init; }

  /// <summary>
  /// Re-encode this camera's finished clips with ROI, keeping the detector's boxes sharp
  /// and spending fewer bits elsewhere. Does nothing for clips that have no boxes.
  /// </summary>
  public bool RoiTranscode { get; init; }

  /// <summary>
  /// Hours a clip stays untouched before it may be re-encoded; 0 means as soon as the
  /// pass notices it. Fractional -- the useful values are as often minutes as hours.
  /// </summary>
  public double RoiDelayHours { get; init; }

  /// <summary>
  /// The band of ROI coverage -- the share of a clip's duration that has boxes on it --
  /// within which re-encoding is worth doing. 0 turns a bound off, so a camera that has
  /// never been configured is not silently excluded from the pass.
  /// </summary>
  public double RoiMinCoverage { get; init; }
  /// <summary><see cref="RoiMinCoverage"/>; 0 means no ceiling.</summary>
  public double RoiMaxCoverage { get; init; }

  /// <summary>Filled by web_vms.Server when merging FS retention status.</summary>
  public bool? ActiveRetentionWindow { get; init; }
  /// <summary>An active retention window with no specific stream retains every stream.</summary>
  public bool? AllStreamsRetained { get; init; }
  /// <summary>Specific stream_names currently retained (an alarm scoped to those streams).</summary>
  public IReadOnlyList<string>? RetainedStreams { get; init; }
  public IReadOnlyList<CameraStreamDto> Streams { get; init; } = [];
  /// <summary>Assigned MediaMTX server ID (uuid).</summary>
  public string? MediaMtxServerId { get; init; }
  /// <summary>Read-only mirror of the assigned media router's STUN URL, used by the browser to
  /// build its WHEP ICE config. null = no router assigned; empty = no STUN (LAN only).</summary>
  public string? EffectiveWebrtcStunUrl { get; init; }
  public bool OnvifEventsEnabled { get; init; }
  public bool HasOnvifFilters    { get; init; }
  public bool OnvifManaged       { get; init; }
  public bool? Disabled          { get; init; }
  /// <summary>True when the camera has an enabled CV watch; filled by web_vms.Server when listing
  /// cameras. Drives whether the live-video bbox overlay toggle is offered. Not to be confused with
  /// the device's own ONVIF Analytics service — that is HasAnalyticsService on the capabilities.</summary>
  public bool CvWatchEnabled     { get; init; }
  /// <summary>Recording schedules assigned to this camera (uuids).</summary>
  public IReadOnlyList<string> ScheduleIds { get; init; } = [];
  /// <summary>True while one of the assigned schedule windows is open. Recomputed by VmsCfg on
  /// every read, never stored. While true the camera records continuously, overriding both the
  /// stream profile and any manual or alarm override.</summary>
  public bool ScheduleActive     { get; init; }

  /// Whether the camera is armed. Runtime state rather than configuration, carried here so the
  /// monitor's per-camera poll is one request instead of two — with the bbox overlays on, their two
  /// SSE streams already hold two of the six connections a browser allows per origin, and a second
  /// poll per camera was enough to exhaust the rest. Only GetCameraState fills it in.
  public bool? Armed             { get; init; }
}
