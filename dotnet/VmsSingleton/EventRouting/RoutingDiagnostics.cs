using VmsLog;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The router's two reports into the debug registry: a run that failed for good and a reaction
/// stopped by the causation guard. Best effort and non-persistent, like every entity-state report:
/// the log is the durable record, this is what an operator sees on the debug tab.
/// </summary>
public sealed class EntityStateRoutingDiagnostics(EntityStatePublisher publisher) : IRoutingDiagnostics
{
  public void Report(string entityType, string entityId, object state) =>
    publisher.Publish(entityType, entityId, state);
}
