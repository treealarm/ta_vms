namespace SquareIntegrationClient;

// Producer-side contract for pushing vms_rec's camera tree into Square (see
// docs/square-integration-plan.md). Square is the read-only consumer here: it never creates or
// edits objects pushed through this interface, it only displays them.
public interface ISquareIntegration
{
  bool Enabled { get; }

  Task RegisterRoot(string appId, string name, double lat, double lon);

  Task UpsertCameraFigure(
    string cameraId,
    string name,
    string parentId,
    double lat,
    double lon,
    IReadOnlyDictionary<string, string> props);

  Task PushCameraProperties(string cameraId, IReadOnlyDictionary<string, string> props);

  Task PushAlarmEvent(string cameraId, DateTime alarmStartTs, string eventName, string? streamName);
}
