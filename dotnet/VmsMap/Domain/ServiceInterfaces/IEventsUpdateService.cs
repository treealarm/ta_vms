
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IEventsUpdateService
  {
    public Task<long> AddEvents(List<EventDTO> events);
  }
}
