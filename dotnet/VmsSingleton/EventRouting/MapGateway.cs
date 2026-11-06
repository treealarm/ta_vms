using Grpc.Core;
using Vms.Map;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The journal and the alarm entity, over VmsMap's gRPC.
/// </summary>
/// <remarks>
/// The router never trusts the bus payload: it carries an id, and the event is read from the
/// journal. The same holds for arming — it is VmsMap's answer, not the router's memory.
/// </remarks>
public sealed class MapGateway(MapService.MapServiceClient map) : IMapGateway
{
  private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

  public async Task<EventFacts?> GetEventAsync(Guid id, CancellationToken ct)
  {
    try
    {
      var evt = await map.GetEventAsync(
        new GetEventRequest { Id = id.ToString() },
        deadline: DateTime.UtcNow.Add(CallTimeout),
        cancellationToken: ct);

      return ToFacts(evt);
    }
    catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
    {
      return null;
    }
  }

  public async Task<bool?> GetArmedAsync(string objectId, CancellationToken ct)
  {
    var response = await map.GetNodeArmingAsync(
      new GetNodeArmingRequest { ObjectId = objectId },
      deadline: DateTime.UtcNow.Add(CallTimeout),
      cancellationToken: ct);

    // A space or a server has no arming of its own; a rule that asks about arming must not fire on
    // an answer that does not exist.
    return response.Found ? response.Arming.Armed : null;
  }

  public async Task EmitEventAsync(EmitEventCommand command, CancellationToken ct)
  {
    var evt = new Event
    {
      Id = command.Id.ToString(),
      ObjectId = command.ObjectId,
      EventName = command.EventName,
      Param0 = command.Param0,
      Param1 = command.Param1,
    };

    evt.ExtraProps.AddRange(command.Props.Select(prop => new ExtraProperty
    {
      PropName = prop.Key,
      StrVal = prop.Value,
    }));

    await map.UpdateEventsAsync(
      new EventList { Events = { evt } },
      deadline: DateTime.UtcNow.Add(CallTimeout),
      cancellationToken: ct);
  }

  public async Task RaiseAlarmAsync(RaiseAlarmCommand command, CancellationToken ct) =>
    await map.RaiseAlarmAsync(
      new RaiseAlarmRequest
      {
        ObjectId = command.ObjectId.ToString(),
        Source = command.Source,
        DedupKey = command.DedupKey,
        SubscriptionId = command.SubscriptionId.ToString(),
        SourceEventId = command.SourceEventId.ToString(),
      },
      deadline: DateTime.UtcNow.Add(CallTimeout),
      cancellationToken: ct);

  private static EventFacts ToFacts(Event evt)
  {
    var props = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var prop in evt.ExtraProps)
      props[prop.PropName] = prop.StrVal;

    return new EventFacts(
      evt.Id, evt.ObjectId, evt.EventName, evt.Param0, evt.Param1, props);
  }
}
