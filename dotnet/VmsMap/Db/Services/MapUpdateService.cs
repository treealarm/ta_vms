using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private readonly IMapService _mapService;
    private readonly ITracksUpdateService _trackUpdateService;
    private readonly IStatesUpdateService _statesUpdateService;

    public MapUpdateService(
      IMapService mapService,
      ITracksUpdateService trackUpdateService,
      IStatesUpdateService statesUpdateService)
    {
      _mapService = mapService;
      _trackUpdateService = trackUpdateService;
      _statesUpdateService = statesUpdateService;
    }

    public async Task<List<BaseMarkerDTO>> UpdateObjects(List<BaseMarkerDTO> objects)
    {
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

      await _mapService.UpdateHierarchyAsync(objects);

      affected.UnionWith(objects.Select(o => o.parent_id).Where(p => !string.IsNullOrEmpty(p))!);
      await _statesUpdateService.RefreshAlarmsAsync(affected);

      return objects;
    }

    public async Task<ObjPropsDTO?> UpdateProperties(ObjPropsDTO updatedMarker)
    {
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

      return statMarkers;
    }

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
