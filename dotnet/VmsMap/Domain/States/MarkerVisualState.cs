using System.Collections.Generic;

namespace VmsMap.Domain
{
  public class MarkersVisualStatesDTO
  {
    public List<ObjectStateDTO>? states { get; set; }
    public List<ObjectStateDescriptionDTO>? states_descr { get; set; }
    public List<AlarmState>? alarmed_objects { get; set; }
  }
}
