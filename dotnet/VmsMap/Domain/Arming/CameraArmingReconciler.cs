using System;
using System.Collections.Generic;
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
  /// </remarks>
  public sealed class CameraArmingReconciler
  {
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
    public async Task<bool> ReconcileAsync(string cameraId, CancellationToken ct)
    {
      var state = await _cameraState.GetStateAsync(cameraId, ct);
      var mode = (await _settings.GetAsync(CameraObjectId.For(cameraId), ct)).Mode;

      var want = mode switch
      {
        ArmMode.Armed => true,
        ArmMode.Disarmed => false,
        _ => await _arming.ZoneArmedAsync(cameraId, ct),
      };
      if (state?.Armed == want)
        return false;

      var changed = await _cfg.SetCamArmedAsync(cameraId, want, ct);
      if (changed)
        _logger.LogInformation(
          "Camera '{CameraId}' armed={Armed} by {Source}", cameraId, want,
          string.Equals(mode, ArmMode.Inherit, StringComparison.Ordinal) ? "the zone it stands in" : "its own mode");
      return changed;
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
