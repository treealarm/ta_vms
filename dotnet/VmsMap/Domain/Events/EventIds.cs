namespace VmsMap.Domain
{
  /// <summary>
  /// Deterministic event ids. An event that can be redelivered — a device event, a reconciled
  /// snapshot — must keep its id across deliveries, or the journal's primary key stops being a
  /// deduplication key and the same transition is written twice. UUIDv5 is not needed: the map
  /// already derives stable ids with MD5 (ObjectIdGenerator), and this is an identity, not a
  /// signature.
  /// </summary>
  public static class EventIds
  {
    /// <summary>One device transition, identified by its place in the publisher's sequence.</summary>
    public static string ForDeviceEvent(
      string sourceKind, string sourceId, string partKind, string token, string epoch, long seq) =>
      ObjectIdGenerator.GenerateObjectId(
        $"{sourceKind}|{sourceId}|{partKind}|{token}|{epoch}|{seq}", "device_event");

    /// <summary>
    /// A difference found by reconciling a snapshot. It has no sequence number, so the sender's
    /// observation instant is part of the identity.
    /// </summary>
    public static string ForSnapshot(
      string sourceKind, string sourceId, string partKind, string token,
      string epoch, System.DateTimeOffset observedAt) =>
      ObjectIdGenerator.GenerateObjectId(
        $"{sourceKind}|{sourceId}|{partKind}|{token}|{epoch}|{observedAt.UtcTicks}",
        "device_snapshot");

    /// <summary>
    /// One persisted detection, named by the analytics database's own row id. The publisher
    /// delivers at least once, so this is what keeps a redelivery from being a second journal
    /// row — and a second reaction of the Router.
    /// </summary>
    public static string ForAnalytics(long detectionId) =>
      ObjectIdGenerator.GenerateObjectId($"analytics|{detectionId}", "analytics_event");
  }
}
