using System.Collections.Generic;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Domain
{
  public interface IStatesUpdateService
  {
    public Task<bool> UpdateStates(List<ObjectStateDTO> newObjs);
    public Task<long> UpdateStateDescrs(List<ObjectStateDescriptionDTO> newObjs);

    // Recomputes the alarms of the named objects and their ancestors without changing any state:
    // a node that moved or was deleted changes what its parents count, and its own row is gone.
    public Task RefreshAlarmsAsync(IReadOnlyCollection<string> objectIds);
  }
}
