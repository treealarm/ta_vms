namespace web_vms.Types.Dto;

/// <summary>
/// Every source that can raise a detection for one camera, in the shape the camera's Guard tab
/// renders. Configuration comes from VmsCfg in one call; the one piece that is device state rather
/// than configuration is merged in by the gateway.
/// </summary>
public record CameraAnalyticsSourcesDto
{
  public required string CameraId { get; init; }
  /// <summary>Null when the camera has no analytics watch at all — different from having one that is switched off.</summary>
  public CvSourceDto? Cv { get; init; }
  public required OnvifMetadataSourceDto OnvifMetadata { get; init; }
  public required OnvifEventsSourceDto OnvifEvents { get; init; }
}

/// <summary>The server-side CV detector (video_a) watching this camera.</summary>
public record CvSourceDto
{
  public bool Enabled { get; init; }
  public string StreamName { get; init; } = string.Empty;
  public string[] Classes { get; init; } = [];
  public float MinConfidence { get; init; }
  public uint SampleFps { get; init; }
  public uint TimeoutSeconds { get; init; }
  public bool AttachDebugCrops { get; init; }
  public bool StoreEmbeddings { get; init; }
}

/// <summary>
/// The camera's own object boxes. Two halves that must not be collapsed into one: <see cref="Accepted"/>
/// is our own setting in VmsCfg, <see cref="DeviceEmits"/> is what the camera is actually doing.
/// </summary>
public record OnvifMetadataSourceDto
{
  /// <summary>Whether we consume the boxes the camera sends (camera_setup.onvif_metadata_disabled, inverted).</summary>
  public bool Accepted { get; init; }
  /// <summary>
  /// Whether the device is configured to stream boxes at all. Null means VmsOnvif has not probed
  /// this camera yet — deliberately tri-state, so the UI can show "unknown" instead of a confident
  /// "no" it cannot back up.
  /// </summary>
  public bool? DeviceEmits { get; init; }
}

/// <summary>The camera's ONVIF event subscription and how many alarm filters are written against it.</summary>
public record OnvifEventsSourceDto
{
  /// <summary>False when the camera has no subscription row at all — not the same as being managed and unsubscribed.</summary>
  public bool Managed { get; init; }
  public bool Subscribed { get; init; }
  public int PollTimeoutSeconds { get; init; }
  public int MessageLimit { get; init; }
  public int FilterCount { get; init; }
}
