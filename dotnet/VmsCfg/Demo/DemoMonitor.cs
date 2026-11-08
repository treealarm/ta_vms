using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg.Demo;

/// A camera and the cell of the wall it fills. Cells are named by index in row-major order, so a
/// wall of pairs leaves a gap between one row and the next instead of packing them.
public sealed record DemoMonitorSlot(int SlotIndex, string CameraId);

/// What one install of the demo monitor did: the cameras the wall now shows, the cell of each, and
/// the ones the deployment does not have yet. Kept apart from the database work so the choice is
/// testable without one.
public sealed record DemoMonitorPlan(
  string MonitorId,
  string Name,
  string Layout,
  IReadOnlyList<DemoMonitorSlot> Slots,
  IReadOnlyList<string> Skipped)
{
  /// The cameras the wall shows, in the order its cells run. What the wall may show: the pool of
  /// the board, as distinct from the cells it has filled.
  public IReadOnlyList<string> Cameras => [.. Slots.Select(slot => slot.CameraId)];
}

/// The demo's video wall. The map world belongs to VmsMap; a wall is camera configuration, so it
/// belongs here beside the cameras, under the same `DEMO_CONFIG` flag.
///
/// The wall keeps one fixed id: installing again updates the same rows instead of adding a second
/// "UMD DC" beside the first. A camera the deployment does not have yet is skipped and reported —
/// the world is valid without it, and the button installs it once the camera exists.
public sealed class DemoMonitor(IDbContextFactory<VmsCfgDbContext> dbFactory, CameraStorage storage)
{
    public const string DemoKey = "umd-datacenter";
    public const string Name = "UMD DC";
    // A 3x3 grid: a row per pair — master beside the slave it aims — and the camera on the car
    // alone on the last row. The right-hand cells and the corners of the last row stay empty: the
    // wall shows the demo's five cameras, not a filled grid.
    public const string Layout = "3x3";
    // The cells of that grid. Cleared before every install, so a camera an earlier layout left in
    // a cell does not stay behind.
    private const int LayoutCells = 3 * 3;

    // A constant of its own, not a hash: this row is a configuration object like any other, and a
    // second install has to name the same one.
    public static readonly Guid MonitorId = new("6f0b8d6e-5f2b-4a6a-9c1e-2d3a4b5c6d7e");

    // Where each camera stands, by the names the demo world gives them. OnvifSim's cameras, as
    // tests/onvifsim provision creates them: master and slave of the hall in the first row, master
    // and slave of the yard in the second, and the camera on the car in the first cell of the
    // third. The cell is named, not derived from the order: a row a pair does not fill would
    // otherwise pull the next pair up beside it.
    private static readonly (int Slot, string Name)[] CameraOrder =
    [
        (0, "sim-master"), (1, "sim-ptz"),
        (3, "sim-master2"), (4, "sim-ptz2"),
        (6, "sim-vehicle"),
    ];

    /// Which of the demo cameras exist, and where each stands. Pure: the same cameras give the same
    /// plan, which is what makes a second install a no-op on the rows. `Slots` names the ids the
    /// wall stores; `Skipped` stays names, because it is what the operator is told.
    public static DemoMonitorPlan Plan(IEnumerable<string> existingCameraIds)
    {
        var existing = existingCameraIds.ToHashSet(StringComparer.Ordinal);

        var present = CameraOrder.Where(camera => existing.Contains(DemoCameraIds.For(camera.Name))).ToList();

        return new DemoMonitorPlan(
            MonitorId.ToString(),
            Name,
            Layout,
            present.Select(camera => new DemoMonitorSlot(camera.Slot, DemoCameraIds.For(camera.Name))).ToList(),
            CameraOrder.Select(camera => camera.Name).Except(present.Select(camera => camera.Name)).ToList());
    }

    public async Task<DemoMonitorPlan> InstallAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.CameraSetups
            .Select(camera => camera.CameraId)
            .ToListAsync(ct);

        var plan = Plan(existing);

        var monitor = await storage.UpsertMonitorAsync(new MonitorSetup
        {
            MonitorId = plan.MonitorId,
            Name = plan.Name,
            Layout = plan.Layout,
            Kind = CameraStorage.MonitorKindWall,
            Owner = string.Empty,
        }, ct);

        // The wall belongs to the deployment; a filtered update could only mean a row that is not
        // ours under our id, which the constant above makes impossible.
        if (monitor is null)
            throw new InvalidOperationException($"The monitor '{MonitorId}' belongs to someone else");

        // The pool is what the wall may show; the slots are what it shows now.
        await storage.SetMonitorCamerasAsync(plan.MonitorId, plan.Cameras, string.Empty, ct);

        // Every cell is either ours or empty: an install clears the grid before laying it out, so a
        // camera a previous layout put in a cell the wall no longer uses does not stay there.
        for (var slot = 0; slot < LayoutCells; slot += 1)
            await storage.ClearMonitorSlotAsync(plan.MonitorId, slot, string.Empty, ct);
        foreach (var slot in plan.Slots)
            await storage.SetMonitorSlotAsync(plan.MonitorId, slot.SlotIndex, slot.CameraId, string.Empty, ct);

        return plan;
    }

    /// Takes the wall away with the world. Its cameras stay: they belong to the deployment, not
    /// to the demo.
    public Task<bool> RemoveAsync(CancellationToken ct) =>
        storage.RemoveMonitorAsync(MonitorId.ToString(), string.Empty, ct);
}
