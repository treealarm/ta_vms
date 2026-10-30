using System.Collections.Generic;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The write orchestration behind the gRPC write path: what one call has to do to several
  // repositories. The gRPC layer wraps these calls in a transaction, so the steps either all
  // happen or none do — Square saved each one separately and could leave a half-written node.
  internal class MapUpdateService : IMapUpdateService
  {
    private readonly IMapService _mapService;
    private readonly ITracksUpdateService _trackUpdateService;

    public MapUpdateService(IMapService mapService, ITracksUpdateService trackUpdateService)
    {
      _mapService = mapService;
      _trackUpdateService = trackUpdateService;
    }

    public async Task<FiguresDTO?> UpdateFigures(FiguresDTO statMarkers)
    {
      await _trackUpdateService.UpdateFigures(statMarkers);
      return statMarkers;
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

    // Deleting a node deletes its subtree; the side tables follow through their foreign keys.
    public Task<long> Delete(List<string> ids) => _mapService.RemoveAsync(ids);
  }
}
