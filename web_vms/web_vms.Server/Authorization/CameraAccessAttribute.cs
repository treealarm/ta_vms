namespace web_vms.Authorization;

/// <summary>Where the filter reads the camera id from.</summary>
public enum CameraIdSource
{
  /// <summary>Route value or query parameter named <c>cameraId</c>.</summary>
  Route,
  /// <summary>A <c>CameraId</c> property on the named (or single) bound body argument.</summary>
  Body,
  /// <summary>
  /// The action resolves and checks access itself — for lists it filters, and for resources
  /// addressed by clip/event/detection id it has to look the camera up first. The filter only
  /// records that a decision was made.
  /// </summary>
  Action,
}

/// <summary>
/// Declares which permission an action needs. Every action must carry this or
/// <see cref="NoCameraAccessAttribute"/>; anything unannotated is refused by
/// <see cref="CameraAccessFilter"/> rather than quietly exposed.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class CameraAccessAttribute(CameraAction action) : Attribute
{
  public CameraAction Action { get; } = action;
  public CameraIdSource Source { get; init; } = CameraIdSource.Route;
  /// <summary>Route/query key or body argument name, when it isn't the default.</summary>
  public string? Name { get; init; }
}

/// <summary>
/// Marks an action that legitimately isn't about any one camera (logs, entity state, server list).
/// The reason is required so the exemption is a decision on the record, not an oversight.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoCameraAccessAttribute(string reason) : Attribute
{
  public string Reason { get; } = reason;
}
