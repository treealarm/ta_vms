
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IDiagramServiceInternal : IDiagramService
  {
    public Task UpdateListAsync(List<DiagramDTO> obj2UpdateIn);
    public Task RemoveAsync(List<string> ids);
  }
  public interface IDiagramService
  {    
    public Task<Dictionary<string, DiagramDTO>> GetListByIdsAsync(List<string> ids);
  }
}
