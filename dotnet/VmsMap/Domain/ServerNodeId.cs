namespace VmsMap.Domain
{
  // The tree node of a node in the deployment's registry: one per media server, named after it
  // (`vms`), and the parent the canonical nodes of its cameras hang under.
  public static class ServerNodeId
  {
    private const string Version = "server";

    public static string For(string serverId) =>
      ObjectIdGenerator.GenerateObjectId(serverId, Version);
  }
}
