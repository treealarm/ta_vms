
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IDiagramServiceInternal : IDiagramService
  {
    public Task UpdateListAsync(List<DiagramDTO> obj2UpdateIn);
    /// <summary>
    /// Writes only the content fields — the type and the picture — of the rows named. A null field
    /// means "not asked" and is left alone; an empty one clears it.
    /// </summary>
    public Task UpdateContentAsync(List<DiagramDTO> rows);
    public Task RemoveAsync(List<string> ids);
  }
  public interface IDiagramService
  {    
    public Task<Dictionary<string, DiagramDTO>> GetListByIdsAsync(List<string> ids);
  }
}
