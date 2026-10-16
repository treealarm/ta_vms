namespace web_vms.Types.Dto;

/// <summary>One box on the live overlay, as the browser names it.</summary>
/// <remarks>
/// <see cref="Kind"/> travels as the same string the SSE overlay carries ("person", "face", ...);
/// AnalyticsKindMapper turns it into the shared enum at this boundary, which is the only place
/// strings and enum meet.
/// </remarks>
public sealed record TrackingTargetDto(string Source, long TrackId, string Kind);

/// <summary>Point a self-guiding camera at one object.</summary>
/// <param name="Target">
/// Null hands the camera back to its configured priorities. Either way the override is temporary:
/// it lapses when the object is finally lost.
/// </param>
public sealed record SetPtzTargetDto(TrackingTargetDto? Target);

/// <summary>How much longer to keep automatic aiming off a camera. Zero releases it.</summary>
public sealed record PtzHoldDto(uint Seconds);

/// <summary>Just the on/off switch, for the monitor's button.</summary>
public sealed record PtzTrackingEnabledDto(bool Enabled);

/// <summary>Whether a camera follows objects on its own, and which ones.</summary>
public sealed record PtzTrackingPolicyDto
{
  public bool Enabled { get; init; }
  public IReadOnlyList<string> Kinds { get; init; } = [];
  public IReadOnlyList<string> Sources { get; init; } = [];
  /// <summary>Whose objects to follow. Null is the camera's own.</summary>
  public string? MasterCameraId { get; init; }
}

public sealed record PtzTrackingStateDto
{
  public required string CameraId { get; init; }
  public bool Active { get; init; }
  public TrackingTargetDto? Target { get; init; }
  /// <summary>The operator named this target, rather than it being picked up automatically.</summary>
  public bool ExplicitTarget { get; init; }
  public DateTime? TargetSeenAt { get; init; }
  /// <summary>Last PTZ failure; empty while the camera is obeying.</summary>
  public string LastError { get; init; } = string.Empty;
  /// <summary>Whose picture this camera follows; null when it is its own.</summary>
  public string? MasterCameraId { get; init; }
}
