using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg.Demo;

/// What one install of the demo monitor did: the cameras the wall now shows and the ones the
/// deployment does not have yet. Kept apart from the database work so the choice is testable
/// without one.
public sealed record DemoMonitorPlan(
  string MonitorId,
  string Name,
  string Layout,
  IReadOnlyList<string> Cameras,
  IReadOnlyList<string> Skipped);

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
    // alone on the last row. The four corners of the bottom rows stay empty: the wall shows the
    // demo's five cameras, not a filled grid.
    public const string Layout = "3x3";

    // A constant of its own, not a hash: this row is a configuration object like any other, and a
    // second install has to name the same one.
    public static readonly Guid MonitorId = new("6f0b8d6e-5f2b-4a6a-9c1e-2d3a4b5c6d7e");

    // The order the wall shows them in, by the names the demo world gives them. OnvifSim's
    // cameras, as tests/onvifsim provision creates them: master and slave of the hall on the first
    // row, master and slave of the yard on the second, and the camera on the car on the third.
    private static readonly string[] CameraOrder =
      ["sim-master", "sim-ptz", "sim-master2", "sim-ptz2", "sim-vehicle"];

    /// Which of the demo cameras exist, in wall order. Pure: the same cameras give the same plan,
    /// which is what makes a second install a no-op on the rows. `Cameras` are the ids the wall
    /// stores; `Skipped` stays names, because it is what the operator is told.
    public static DemoMonitorPlan Plan(IEnumerable<string> existingCameraIds)
    {
        var existing = existingCameraIds.ToHashSet(StringComparer.Ordinal);

        var present = CameraOrder.Where(name => existing.Contains(DemoCameraIds.For(name))).ToList();

        return new DemoMonitorPlan(
            MonitorId.ToString(),
            Name,
            Layout,
            present.Select(DemoCameraIds.For).ToList(),
            CameraOrder.Except(present).ToList());
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

        await storage.SetMonitorCamerasAsync(plan.MonitorId, plan.Cameras, string.Empty, ct);

        // The pool is what the wall may show; the slots are what it shows now, in the wall's order.
        for (var slot = 0; slot < plan.Cameras.Count; slot += 1)
            await storage.SetMonitorSlotAsync(plan.MonitorId, slot, plan.Cameras[slot], string.Empty, ct);

        return plan;
    }

    /// Takes the wall away with the world. Its cameras stay: they belong to the deployment, not
    /// to the demo.
    public Task<bool> RemoveAsync(CancellationToken ct) =>
        storage.RemoveMonitorAsync(MonitorId.ToString(), string.Empty, ct);
}
