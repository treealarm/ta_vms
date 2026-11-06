
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VmsMap.Domain
{
  public interface IEventsService
  {
    /// <summary>
    /// Inserts the events that are not in the journal yet, and answers the ids it actually inserted.
    /// A redelivery is a no-op, which is what makes deterministic ids and the outbox row written
    /// beside them safe to replay.
    /// </summary>
    Task<IReadOnlyList<string>> InsertManyAsync(List<EventDTO> newObjs);
    Task<List<EventDTO>> GetEventsByFilter(SearchEventFilterDTO filter_in);

    /// <summary>One journal row with its props, or null when the journal has no such id.</summary>
    Task<EventDTO?> GetByIdAsync(Guid id);
  }
}
