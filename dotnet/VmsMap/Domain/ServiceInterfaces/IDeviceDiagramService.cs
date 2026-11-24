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
  /// A placement or a binding the map cannot make: the canvas does not exist, the region is not the
  /// canvas type's, a node already stands in it, or the canvas is the diagram itself. The message
  /// says which, because the administrator is the one who can fix it.
  /// </summary>
  /// <remarks>
  /// `Conflicting` tells the two kinds apart for the wire: the request is well formed and the map's
  /// current state refuses it — the region taken, the device already having a diagram for the view —
  /// answers `FailedPrecondition`; a request that names something that does not exist or cannot be
  /// one answers `InvalidArgument`.
  /// </remarks>
  public sealed class DeviceDiagramException(string message, bool conflicting = false)
    : Exception(message)
  {
    public bool Conflicting { get; } = conflicting;
  }

  /// <summary>What a diagram node is bound to: the device and the view of it the diagram draws.</summary>
  public sealed record DeviceDiagramBinding(string SourceKind, string SourceId, string Slot);

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

    /// <summary>
    /// Binds a diagram the administrator drew to a device: the row that makes the map maintain it,
    /// then the template the device's model names and a managed replica per configured part, all in
    /// one transaction under the device's lock. The diagram keeps its place, its type (a template
    /// named here replaces it, and must describe the device) and what was drawn in it by hand.
    /// Binding a diagram already bound to this device and slot again changes nothing; binding a
    /// node bound to another device, or a device that already has a diagram for this slot, is
    /// refused until the first binding is removed. Returns the diagram's node.
    /// Throws <see cref="DeviceDiagramException"/> when the node is not a diagram of the map, the
    /// device is unknown, or the binding would break one of those rules.
    /// </summary>
    Task<string?> BindAsync(
      string nodeId, string sourceKind, string sourceId, string? diagramTypeId, string slot,
      CancellationToken ct);

    /// <summary>
    /// Takes a diagram away from its device: the replicas it made and the rows that name them, then
    /// the row itself. The node, its type, its place and what was drawn in it by hand stay, and it
    /// is an ordinary mnemoscheme again — its representations keep showing it. Idempotent: a node
    /// that is not bound answers false and changes nothing.
    /// </summary>
    Task<bool> UnbindAsync(string nodeId, CancellationToken ct);

    /// <summary>What the node is bound to, or null when it is an ordinary mnemoscheme.</summary>
    Task<DeviceDiagramBinding?> FindBindingAsync(string nodeId, CancellationToken ct);
  }
}
