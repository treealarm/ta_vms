using System.Threading;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  /// <summary>
  /// Keeps a device's diagram in step with the template it was drawn from: one node per
  /// (device, slot) whose children are replicas of the device's configured parts, placed in the
  /// template's regions.
  /// </summary>
  public interface IDeviceDiagramService
  {
    public const string DefaultSlot = "default";

    /// <summary>
    /// Reconciles the diagram of one device: adopts a template when the model matches and one
    /// attaches itself, creates the diagram if there is one to create, and brings the managed
    /// replicas in line with the device's configured parts. Idempotent; a device with no template
    /// and no diagram costs one query.
    /// </summary>
    Task EnsureAsync(string sourceKind, string sourceId, string slot, CancellationToken ct);

    /// <summary>
    /// Attaches a chosen type to a device that has no diagram yet — the administrator's path for a
    /// model no template auto-attaches to. The diagram it creates is maintained exactly like an
    /// automatic one; <c>auto_created</c> only records who asked.
    /// </summary>
    Task AttachAsync(string sourceKind, string sourceId, string diagramTypeId, string slot, CancellationToken ct);
  }
}
