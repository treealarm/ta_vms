using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The write orchestration behind the gRPC write path: what one call has to do to several
  // repositories. The gRPC layer wraps these calls in a transaction, so the steps either all
  // happen or none do — Square saved each one separately and could leave a half-written node.
  //
  // Moving or deleting a node changes what its parents count, so the alarm rows of the affected
  // parents are recomputed here rather than left to the caller.
  internal class MapUpdateService : IMapUpdateService
  {
    private readonly VmsMapDbContext _db;
    private readonly IMapService _mapService;
    private readonly ITracksUpdateService _trackUpdateService;
    private readonly IStatesUpdateService _statesUpdateService;

    public MapUpdateService(
      VmsMapDbContext db,
      IMapService mapService,
      ITracksUpdateService trackUpdateService,
      IStatesUpdateService statesUpdateService)
    {
      _db = db;
      _mapService = mapService;
      _trackUpdateService = trackUpdateService;
      _statesUpdateService = statesUpdateService;
    }

    public async Task<List<BaseMarkerDTO>> UpdateObjects(List<BaseMarkerDTO> objects)
    {
      // The alarm recompute at the end takes the alarm lock; the hierarchy write takes its own
      // after it. Both are taken here, in that order, so a caller that has a transaction gets one
      // lock order, and a caller without one still gets both inside this call's transaction.
      var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync()
        : null;

      var affected = new HashSet<string>();

      // The old parent stops counting a node that moved away.
      foreach (var dto in objects.Where(o => !string.IsNullOrEmpty(o.id)))
      {
        var existing = await _mapService.GetAsync(dto.id);
        if (!string.IsNullOrEmpty(existing?.parent_id))
        {
          affected.Add(existing!.parent_id!);
        }
      }

      await MapLocks.AcquireAlarmAsync(_db);
      await _mapService.UpdateHierarchyAsync(objects);

      affected.UnionWith(objects.Select(o => o.parent_id).Where(p => !string.IsNullOrEmpty(p))!);
      await _statesUpdateService.RefreshAlarmsAsync(affected);

      if (tx is not null)
        await tx.CommitAsync();

      return objects;
    }

    public async Task<ObjPropsDTO?> UpdateProperties(ObjPropsDTO updatedMarker)
    {
      // A property write is not a tree write. The DTO carries no parent or owner, and copying the
      // nulls it defaults to onto the row would move the node to the root -- which the hierarchy
      // rules now refuse for a plan, and which was never what the caller asked for anyway.
      var current = await _mapService.GetAsync(updatedMarker.id);
      if (current is not null)
      {
        updatedMarker.parent_id = current.parent_id;
        updatedMarker.owner_id = current.owner_id;
        updatedMarker.name = current.name;
      }

      await _mapService.UpdateHierarchyAsync(new List<BaseMarkerDTO> { updatedMarker });

      var marker = await _mapService.GetAsync(updatedMarker.id);

      if (marker is null)
      {
        return null;
      }

      await _mapService.UpdatePropAsync(new[] { updatedMarker });

      return updatedMarker;
    }

    public async Task<FiguresDTO?> UpdateFigures(FiguresDTO statMarkers)
    {
      // A figure write writes the tree row too, so it takes the same two locks in the same order
      // as UpdateObjects: alarm first, hierarchy second.
      var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync()
        : null;

      await MapLocks.AcquireAlarmAsync(_db);
      await _trackUpdateService.UpdateFigures(statMarkers);

      var affected = statMarkers.figs
        .Select(f => f.parent_id)
        .Where(p => !string.IsNullOrEmpty(p))
        .Distinct()
        .ToList();

      if (affected.Count > 0)
      {
        await _statesUpdateService.RefreshAlarmsAsync(affected!);
      }

      if (tx is not null)
        await tx.CommitAsync();

      return statMarkers;
    }

    public Task RefreshPlacementMirrorsAsync(IReadOnlyCollection<string> cameraIds) =>
      _statesUpdateService.RefreshPlacementMirrorsAsync(cameraIds);

    public async Task<long> Delete(List<string> ids)
    {
      var affected = new HashSet<string>();

      foreach (var id in ids)
      {
        var existing = await _mapService.GetAsync(id);
        if (!string.IsNullOrEmpty(existing?.parent_id))
        {
          affected.Add(existing!.parent_id!);
        }
      }

      var removed = await _mapService.RemoveAsync(ids);
      await _statesUpdateService.RefreshAlarmsAsync(affected);

      return removed;
    }
  }
}
