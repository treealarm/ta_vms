using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IRightService
  {
    public Task<Dictionary<string, List<RightDTO>>> GetListByIdsAsync(List<string> ids);

    // Every stored row, for the administrator's matrix and the compatibility API. The map itself
    // reads one user's effective set through IRightsService, not this.
    public Task<List<RightDTO>> GetAllAsync();
  }

  public interface IRightUpdateService
  {
    // Deletes exactly the rows named by (object_id, subject, action); the rest of an object's
    // rights stay.
    public Task<long> Delete(List<RightDTO> keys);
    public Task<List<RightDTO>> Update(List<RightDTO> newObjs);
  }

  public interface IRightServiceInternal : IRightService, IRightUpdateService
  {
  }
}
