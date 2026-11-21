using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VmsMap.Clients;
using VmsMap.Db;

namespace VmsMap.Domain.Arming
{
  /// <summary>
  /// Keeps a camera's effective flag equal to what its zone says, for the cameras that inherit.
  /// </summary>
  /// <remarks>
  /// The zone is the map's own state and the flag is VmsCfg's, read by services that cannot see the
  /// map — VmsOnvif's retention gate and the routing gateway. So the camera is not resolved where
  /// it is read: the map derives the answer and writes it, and this class is the one place that
  /// does. A camera that does not inherit is never touched: its mode is a decision, and the flag
  /// with it.
  /// <para>
  /// The write is skipped when the value already matches, and VmsCfg announces only a write that
  /// changed a row, so the round "the map writes, the event arrives, the map looks again" ends
  /// after one extra hop instead of running forever.
  /// </para>
  /// <para>
  /// Every write is read back and derived again before the call returns: the answer is computed
  /// from data that another writer — a placement move, a zone change, the demo world's install —
  /// may change between the read and the write, and the last writer would otherwise leave a stale
  /// flag until the next trigger. A write that lands after another writer's is therefore corrected
  /// by the pass that follows it, and each writer's own post-commit pass closes the same window
  /// from the other side. The map reads that settle the answer are taken as one snapshot, so a
  /// verification cannot mistake a half-old tree for a settled one; the snapshot ends before the
  /// flag is written, because a transaction is never held across the gRPC call.
  /// </para>
  /// </remarks>
  public sealed class CameraArmingReconciler
  {
    /// <summary>
    /// How many times a flag may be written in one call. Each write is followed by a read, so the
    /// last possible write is verified too; a pass limit of writes, not of reads, is what keeps
    /// that true.
    /// </summary>
    internal const int MaxWrites = 3;

    private readonly VmsMapDbContext _db;
    private readonly EffectiveArmingResolver _arming;
    private readonly DeviceObjectSettingsStore _settings;
    private readonly ICameraStateSource _cameraState;
    private readonly ICfgArmingWriter _cfg;
    private readonly IMapService _map;
    private readonly ILogger<CameraArmingReconciler> _logger;

    public CameraArmingReconciler(
      VmsMapDbContext db,
      EffectiveArmingResolver arming,
      DeviceObjectSettingsStore settings,
      ICameraStateSource cameraState,
      ICfgArmingWriter cfg,
      IMapService map,
      ILogger<CameraArmingReconciler> logger)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _arming = arming ?? throw new ArgumentNullException(nameof(arming));
      _settings = settings ?? throw new ArgumentNullException(nameof(settings));
      _cameraState = cameraState ?? throw new ArgumentNullException(nameof(cameraState));
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Brings one camera's flag in line with its own mode or with its zone; answers whether it had
    /// to write. An explicit mode is the camera's decision and wins over the zone; only `inherit`
    /// asks the zone.
    /// </summary>
    /// <remarks>
    /// The answer is derived again after every write, up to <see cref="MaxWrites"/> writes, so a
    /// value that another writer has already replaced is corrected rather than left standing. The
    /// usual case still costs one read and no write: the loop ends as soon as the flag and the
    /// map agree.
    /// </remarks>
    public async Task<bool> ReconcileAsync(string cameraId, CancellationToken ct)
    {
      var wrote = false;
      for (var pass = 0; ; pass += 1)
      {
        var state = await _cameraState.GetStateAsync(cameraId, ct);
        var (mode, want) = await DeriveAsync(cameraId, ct);
        if (state?.Armed == want)
          return wrote;

        if (pass == MaxWrites)
        {
          _logger.LogWarning(
            "Camera '{CameraId}' arming did not settle after {Writes} write(s)", cameraId, MaxWrites);
          return wrote;
        }

        var changed = await _cfg.SetCamArmedAsync(cameraId, want, ct);
        if (!changed)
          return wrote;

        wrote = true;
        _logger.LogInformation(
          "Camera '{CameraId}' armed={Armed} by {Source}", cameraId, want,
          string.Equals(mode, ArmMode.Inherit, StringComparison.Ordinal) ? "the zone it stands in" : "its own mode");
      }
    }

    /// <summary>
    /// The camera's own mode and the flag it implies, from the map alone. The reads make one
    /// snapshot: the resolver walks several tables, and a placement committed between two of its
    /// queries must not be half-seen. The snapshot is a read-only transaction that ends here,
    /// before the caller writes the flag — no transaction is held across the gRPC call. It is
    /// skipped where the provider has no transactions (the in-memory tests) or when the caller
    /// already opened one.
    /// </summary>
    private async Task<(string Mode, bool Armed)> DeriveAsync(
      string cameraId, CancellationToken ct)
    {
      if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        return await DeriveFromMapAsync(cameraId, ct);

      await using var snapshot = await _db.Database.BeginTransactionAsync(
        IsolationLevel.RepeatableRead, ct);
      var answer = await DeriveFromMapAsync(cameraId, ct);
      await snapshot.CommitAsync(ct);
      return answer;
    }

    private async Task<(string Mode, bool Armed)> DeriveFromMapAsync(
      string cameraId, CancellationToken ct)
    {
      var mode = (await _settings.GetAsync(CameraObjectId.For(cameraId), ct)).Mode;
      var want = mode switch
      {
        ArmMode.Armed => true,
        ArmMode.Disarmed => false,
        _ => await _arming.ZoneArmedAsync(cameraId, ct),
      };
      return (mode, want);
    }

    /// <summary>The same for a page of cameras, one at a time: VmsCfg takes one write apiece.</summary>
    public async Task ReconcileManyAsync(IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      foreach (var cameraId in cameraIds.Distinct(StringComparer.Ordinal))
      {
        try
        {
          await ReconcileAsync(cameraId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // A zone that was armed stays armed for every camera but this one; the next trigger, or
          // the sweep at the next start, retries it.
          _logger.LogWarning(ex, "Setting the arming of camera '{CameraId}' from its zone failed", cameraId);
        }
      }
    }

    /// <summary>The cameras whose reference replica stands in a node's subtree — a zone's devices.</summary>
    public async Task ReconcileSubtreeAsync(string objectId, CancellationToken ct)
    {
      var subtree = await _map.GetSubtreeIdsAsync([objectId]);
      if (subtree.Count == 0)
        return;

      var guids = subtree
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var cameraIds = await _db.CameraPlacements.AsNoTracking()
        .Where(p => p.is_reference && guids.Contains(p.object_id))
        .Select(p => p.camera_id)
        .Distinct()
        .ToListAsync(ct);

      await ReconcileManyAsync(cameraIds, ct);
    }

    /// <summary>
    /// The start-of-service pass: every space with a row says its cameras are what the zone makes
    /// them. Bounded by the zones that are actually set, not by the deployment's cameras — a
    /// hundred thousand cameras are not walked to find the few a zone holds.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
      var zones = await _db.ObjectArming.AsNoTracking()
        .Select(a => a.object_id)
        .ToListAsync(ct);

      foreach (var zone in zones)
        await ReconcileSubtreeAsync(Utils.ConvertGuidToObjectId(zone), ct);
    }
  }
}
