using System;
using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  /// <summary>
  /// Where a device's diagram is asked to stand. `CanvasId` names a space, which takes a point, or a
  /// diagram of the map, which takes a region of its type or a rectangle of its own.
  ///
  /// A space is the diagram's own card on the plan: the canonical node is drawn there. A diagram's
  /// canvas shows the device's diagram without owning it: the request makes a representation — a
  /// replica that stands here and opens on the diagram it represents. A null target means "keep it
  /// with the camera and draw nothing".
  /// </summary>
  public sealed record DiagramTarget(
    string CanvasId,
    string? RegionId = null,
    DiagramCoordDTO? Geometry = null,
    double? Lat = null,
    double? Lon = null);

  /// <summary>
  /// A placement the map cannot make: the canvas does not exist, the region is not the canvas
  /// type's, a node already stands in it, or the canvas is the diagram itself. The message says
  /// which, because the administrator is the one who can fix it.
  /// </summary>
  public sealed class DeviceDiagramException(string message) : Exception(message);

  /// <summary>
  /// Keeps a device's diagram in step with the template it was drawn from: one canonical diagram
  /// per (device, slot) whose children are replicas of the device's configured parts, placed in the
  /// template's regions.
  /// </summary>
  public interface IDeviceDiagramService
  {
    public const string DefaultSlot = "default";

    /// <summary>
    /// Brings an existing diagram in line with the device: adopts a template that now matches its
    /// model, and makes the managed replicas follow the configured parts. Never creates one — a
    /// diagram is a map object, and where it stands is the administrator's to say. Idempotent; a
    /// device with no diagram costs one query.
    /// </summary>
    Task ReconcileDeviceAsync(string sourceKind, string sourceId, string slot, CancellationToken ct);

    /// <summary>
    /// Draws a device on the map: finds the device's canonical diagram — made under the camera's
    /// reference picture when there is none, from the template named or the one that matches the
    /// device's model, an empty sheet when none does — and puts it where the target says: on a
    /// space as the diagram's own card, or on a diagram's canvas as a representation of it, in a
    /// region or at a rectangle. The device's configured parts are bound to the template's regions.
    /// A diagram already standing where it is asked to keeps its place; one already shown in that
    /// region of that canvas is reused rather than copied.
    /// Returns the node that was placed — the representation on a diagram's canvas, the diagram
    /// itself elsewhere — or null when there is nowhere to keep it (a device that is not placed at
    /// all) or the template named is gone. Throws <see cref="DeviceDiagramException"/> when the
    /// request names a place the map cannot give.
    /// </summary>
    Task<string?> PlaceDeviceDiagramAsync(
      string sourceKind, string sourceId, string? diagramTypeId, string slot,
      DiagramTarget? target, CancellationToken ct);
  }
}
