using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IStateService
  {
    public Task<long> UpdateStatesAsync(List<ObjectStateDTO> newObjs);
    public Task<long> UpdateStateDescrsAsync(List<ObjectStateDescriptionDTO> newObjs);
    public Task<Dictionary<string, ObjectStateDTO>> GetStatesAsync(List<string> ids);
    public Task<List<ObjectStateDescriptionDTO>> GetStateDescrAsync(
      List<string> states
    );
    public Task UpdateAlarmStatesAsync(List<AlarmState> alarms);
    public Task<Dictionary<string, AlarmState>> GetAlarmStatesAsync(List<string> ids);
    public Task<List<AlarmState>> GetAlarmedLeafStatesAsync(List<string> ids);
    public Task DropStateAlarms();
    public Task<Dictionary<string, ObjectStateDTO>> GetAlarmedStates(List<string> statesFilter);
    public Task<Dictionary<string, ObjectStateDescriptionDTO>> GetAlarmStatesDescr(List<string>? statesFilter);
  }
}
