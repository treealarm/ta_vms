namespace web_vms.Types.Dto;

/// <summary>One box on the live overlay, as the browser names it.</summary>
/// <remarks>
/// <see cref="Kind"/> travels as the same string the SSE overlay carries ("person", "face", ...);
/// AnalyticsKindMapper turns it into the shared enum at this boundary, which is the only place
/// strings and enum meet.
/// </remarks>
public sealed record TrackingTargetDto(string Source, long TrackId, string Kind);

/// <summary>Turn tracking on or off for a camera, optionally naming what to follow.</summary>
/// <param name="Target">Null asks for the first object that turns up.</param>
public sealed record SetPtzTrackingDto(bool Enabled, TrackingTargetDto? Target);

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
}
