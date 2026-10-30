using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IRightService
  {
    public Task<Dictionary<string, List<RightDTO>>> GetListByIdsAsync(List<string> ids);
  }

  public interface IRightUpdateService
  {
    // Deletes exactly the rows named by (object_id, subject, action); the rest of an object's
    // rights stay.
    public Task<long> Delete(List<RightDTO> keys);
    public Task<List<RightDTO>> Update(List<RightDTO> newObjs);
  }

  internal interface IRightServiceInternal : IRightService, IRightUpdateService
  {
  }
}
