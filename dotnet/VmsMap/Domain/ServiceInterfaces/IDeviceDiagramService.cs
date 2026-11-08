using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  /// <summary>
  /// Where a device's diagram stands on the map: a space, and the point it is drawn at. Without a
  /// point the diagram is put beside the camera's own placement in that space.
  /// </summary>
  public sealed record DiagramSite(string SpaceId, double? Lat = null, double? Lon = null);

  /// <summary>
  /// Keeps a device's diagram in step with the template it was drawn from: one node per
  /// (device, slot) whose children are replicas of the device's configured parts, placed in the
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
    Task EnsureAsync(string sourceKind, string sourceId, string slot, CancellationToken ct);

    /// <summary>
    /// Draws a device on the map: creates its diagram node in the given space, at the given point,
    /// from the template named or the one that matches the device's model — an empty sheet when
    /// none does — and binds the device's configured parts to its regions. A device that already
    /// has a diagram keeps it; the type it was drawn from is replaced when another is named.
    /// Returns the diagram's node id.
    /// </summary>
    Task<string?> AttachAsync(
      string sourceKind, string sourceId, string? diagramTypeId, string slot,
      DiagramSite? site, CancellationToken ct);
  }
}
