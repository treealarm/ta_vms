using System.Collections.Generic;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Domain
{
  public interface IMapUpdateService
  {
    public Task<ObjPropsDTO?> UpdateProperties(ObjPropsDTO updatedMarker);
    public Task<FiguresDTO?> UpdateFigures(FiguresDTO statMarkers);
    // The subtree is deleted with the named nodes; returns the number of nodes removed.
    public Task<long> Delete(List<string> ids);
  }
}
