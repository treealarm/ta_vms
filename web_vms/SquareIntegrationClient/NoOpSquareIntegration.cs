namespace SquareIntegrationClient;

// Used when SQUARE_INTEGRATION_SINK_APP_ID is unset — vms_rec runs fully standalone, with zero
// dependency on Square being present.
public class NoOpSquareIntegration : ISquareIntegration
{
  public bool Enabled => false;

  public Task RegisterRoot(string appId, string name, double lat, double lon) => Task.CompletedTask;

  public Task UpsertCameraFigure(
    string cameraId, string name, string parentId, double lat, double lon,
    IReadOnlyDictionary<string, string> props) => Task.CompletedTask;

  public Task PushCameraProperties(string cameraId, IReadOnlyDictionary<string, string> props) =>
    Task.CompletedTask;

  public Task PushAlarmEvent(string cameraId, DateTime alarmStartTs, string eventName, string? streamName) =>
    Task.CompletedTask;
}
