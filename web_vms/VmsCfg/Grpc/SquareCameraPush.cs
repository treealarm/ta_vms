using SquareIntegrationClient;
using Vms.Cfg;

namespace VmsCfg;

// Shared by CfgServiceImpl (per-camera push on config/state change) and Program.cs's startup
// resync (full re-push of every camera) — kept in one place so the two don't drift.
public static class SquareCameraPush
{
  public static readonly double DefaultLat =
    double.TryParse(Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_DEFAULT_LAT"), out var lat) ? lat : 0;
  public static readonly double DefaultLon =
    double.TryParse(Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_DEFAULT_LON"), out var lon) ? lon : 0;

  public static async Task PushCameraAsync(
    ISquareIntegration square, CameraStorage storage, string appId, string cameraId, string cameraName,
    CancellationToken ct)
  {
    // Playback details (webrtc host/port, stream names) are NOT pushed here anymore — Square's
    // Monitor tab fetches them directly from vms_rec's own GET /api/live/cameras (CORS+JWT already
    // enabled for that), so any future field is available there for free with no push code on
    // either side. Only what the map's own property panel needs stays here.
    var (found, setup) = await storage.GetCameraAsync(cameraId, ct);
    var primaryStream = found ? setup!.Streams.FirstOrDefault(s => s.WebRtc) : null;

    var props = new Dictionary<string, string>
    {
      ["profile_name"] = primaryStream?.RecordingProfile?.Name ?? "",
    };

    await square.UpsertCameraFigure(cameraId, cameraName, appId, DefaultLat, DefaultLon, props);
  }

  public static async Task PushAllCamerasAsync(
    ISquareIntegration square, CameraStorage storage, string appId, ILogger logger, CancellationToken ct)
  {
    await square.RegisterRoot(appId, appId, DefaultLat, DefaultLon);
    foreach (var (serverId, _) in storage.GetServerPairs())
    {
      foreach (var camera in await storage.ListCamerasAsync(serverId, ct))
      {
        // One camera's failure must not abort the rest of the batch — otherwise a single
        // persistently-broken camera blocks every camera after it in iteration order, on every
        // resync attempt (this is what silently dropped pushes for camera ids later in the list).
        try
        {
          await PushCameraAsync(square, storage, appId, camera.CameraId, camera.Name, ct);
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Square push failed for camera '{CameraId}'", camera.CameraId);
        }
      }
    }
  }
}
