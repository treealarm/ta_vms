using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Singleton;

namespace VmsSingleton;

public class SingletonServiceImpl(EntityStateRegistry entityStateRegistry)
  : SingletonService.SingletonServiceBase
{
  public override Task<ListEntityStatesResponse> ListEntityStates(Empty request, ServerCallContext context)
  {
    var response = new ListEntityStatesResponse();
    foreach (var (key, entry) in entityStateRegistry.GetAll())
    {
      response.Entities.Add(new EntityState
      {
        Service = key.Service,
        EntityType = key.EntityType,
        EntityId = key.EntityId,
        StateJson = entry.StateJson,
        LastSeen = Timestamp.FromDateTime(DateTime.SpecifyKind(entry.LastSeenUtc, DateTimeKind.Utc)),
        Stale = entry.Stale,
      });
    }
    return Task.FromResult(response);
  }
}
