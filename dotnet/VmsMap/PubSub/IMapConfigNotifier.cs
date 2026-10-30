namespace VmsMap.PubSub
{
  // Announces that the map's configuration changed. The event is addressing only: it names the
  // nodes that changed and the cameras whose spatial model did, and a subscriber re-reads what it
  // needs. An empty camera list means "everything" -- a space or a landmark moved, and a cached
  // model may be wrong for any camera.
  public interface IMapConfigNotifier
  {
    Task PublishAsync(
      IReadOnlyCollection<string> objectIds,
      IReadOnlyCollection<string> cameraIds,
      CancellationToken ct);
  }
}
