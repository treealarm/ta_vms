using System.Collections.Generic;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Domain
{
  public interface IMapUpdateService
  {
    // Creates, renames or moves nodes; the response carries the ids of the new ones.
    public Task<List<BaseMarkerDTO>> UpdateObjects(List<BaseMarkerDTO> objects);
    public Task<ObjPropsDTO?> UpdateProperties(ObjPropsDTO updatedMarker);
    public Task<FiguresDTO?> UpdateFigures(FiguresDTO statMarkers);
    // The subtree is deleted with the named nodes; returns the number of nodes removed.
    public Task<long> Delete(List<string> ids);
  }
}
