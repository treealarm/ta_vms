

using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IDiagramUpdateService
  {
    Task<List<DiagramDTO>> UpdateDiagrams(List<DiagramDTO> dgrs);
    Task<List<string>> DeleteDiagrams(List<string> dgrs);
  }
}
