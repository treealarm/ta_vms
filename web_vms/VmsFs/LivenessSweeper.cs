using Vms.Cfg;
// VmsFs has its own CameraSetup DTO (CameraSetupCache); this file wants the protobuf one.
using CfgCameraSetup = Vms.Cfg.CameraSetup;

namespace VmsFs;

/// <summary>
/// Notices streams that stopped reporting at all, and removes outage rows that have aged out.
/// </summary>
/// <remarks>
/// This is the half of the design that silence buys: a camera that died and a media server that died
/// look the same from here — no heartbeat — and both become an interval. Which of the two it was is
/// decided by how many streams of the node went quiet at once.
///
/// The expected stream list comes from configuration, not from the database: absence of a message is
/// not a message, so there has to be an independent statement of what ought to be reporting.
/// </remarks>
public sealed class LivenessSweeper(
  SourceLivenessTracker tracker,
  SourceOutageStore outages,
  CfgService.CfgServiceClient cfg,
  ILogger<LivenessSweeper> logger) : IHostedService, IAsyncDisposable
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
  private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

  private static readonly TimeSpan OutageRetention = TimeSpan.FromDays(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_OUTAGE_RETENTION_DAYS"), out var d) && d > 0
      ? d : 90);

  /// <summary>Only this node's cameras. The two VmsFs instances share one database.</summary>
  public string? OwnServerId { get; private set; }

  // Until the node is known, every camera in the system looks like one of ours — and every stream
  // the OTHER node reads looks silent, because its heartbeats go to the other VmsFs. Sweeping then
  // would fabricate an outage for every camera of every other node.
  private volatile bool _scopeReady;

  /// <summary>
  /// Declares which node this instance serves; sweeping does not start until it is called. Pass
  /// null when the deployment has no APP_ID and therefore only one FS instance to scope to.
  /// </summary>
  public void SetScope(string? serverId)
  {
    OwnServerId = serverId;
    _scopeReady = true;
  }

  private readonly CancellationTokenSource _cts = new();
  private Task? _task;
  private DateTime _lastCleanup = DateTime.MinValue;

  public async Task StartAsync(CancellationToken cancellationToken)
  {
    try
    {
      await tracker.LoadOpenAsync(cancellationToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not load open source outages; starting with an empty view");
    }
    _task = RunAsync(_cts.Token);
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        await Task.Delay(Interval, ct);
        await SweepAsync(ct);
        await MaybeCleanupAsync(ct);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "LivenessSweeper fatal error");
    }
  }

  private async Task SweepAsync(CancellationToken ct)
  {
    if (!_scopeReady)
      return;

    List<CfgCameraSetup> cameras;
    try
    {
      // Scoped in the request rather than after the fact: the two VmsFs instances share one
      // database, and a sweeper acting on the other node's cameras would invent outages for streams
      // it never hears from by design.
      var req = new ListCamerasRequest();
      if (OwnServerId is not null)
        req.ServerId = OwnServerId;
      var resp = await cfg.ListCamerasAsync(req, cancellationToken: ct);
      cameras = resp.Cameras.ToList();
    }
    catch (Exception ex)
    {
      // Without the expected list there is nothing to compare silence against — skip this tick
      // rather than invent outages for streams we cannot enumerate.
      logger.LogDebug(ex, "LivenessSweeper: could not list cameras");
      return;
    }

    var now = DateTime.UtcNow;
    var expected = new List<(string Camera, string Stream)>();
    var recordingByKey = new Dictionary<(string Camera, string Stream), bool>();
    foreach (var camera in cameras)
    {
      if (camera.HasDisabled && camera.Disabled)
        continue;
      foreach (var stream in camera.Streams)
      {
        if (stream.HasDisabled && stream.Disabled)
          continue;
        var key = SourceLivenessTracker.Key(camera.CameraId, stream.StreamName);
        expected.Add(key);
        recordingByKey[key] = RecordingExpected(camera, stream);
      }
    }

    var silent = expected.Where(k => !tracker.IsFresh(k, now)).ToList();

    // Every stream of the node quiet at once is the media server, not each camera independently.
    var wholeNodeSilent = expected.Count > 0 && silent.Count == expected.Count;
    var cause = wholeNodeSilent ? "server_down" : "link_lost";

    foreach (var key in silent)
    {
      if (tracker.HasOpenOutage(key))
        continue;
      await tracker.OpenForSilenceAsync(
        key.Camera, key.Stream, cause, recordingByKey.GetValueOrDefault(key), ct);
    }

    // A stream that left the configuration was taken out deliberately — that is not an outage.
    var expectedSet = expected.ToHashSet();
    tracker.PruneTo(expectedSet);
    var ownCameras = cameras.Select(c => c.CameraId).ToHashSet();
    foreach (var (camera, stream) in await outages.ListOpenAsync(ct))
    {
      // Rows of the other node's cameras live in the same table; leave them to their own sweeper.
      if (OwnServerId is not null && !ownCameras.Contains(camera))
        continue;
      if (!expectedSet.Contains(SourceLivenessTracker.Key(camera, stream)))
        await tracker.ForgetAsync(camera, stream, ct);
    }
  }

  /// <summary>
  /// Whether the configuration says this stream should be recording — the same precedence
  /// media_server applies (an open schedule window beats an override, which beats the base
  /// profile). Only a fallback: a live heartbeat carries the observed flag and is preferred.
  /// </summary>
  private static bool RecordingExpected(CfgCameraSetup camera, CameraStream stream)
  {
    if (camera.ScheduleActive)
      return true;
    if (stream.OverrideProfile is { } ovr)
      return ovr.Mode != RecordingMode.RecordingOff;
    if (stream.RecordingProfile is { } profile)
      return profile.Mode != RecordingMode.RecordingOff;
    return false;
  }

  private async Task MaybeCleanupAsync(CancellationToken ct)
  {
    if (DateTime.UtcNow - _lastCleanup < CleanupInterval)
      return;
    _lastCleanup = DateTime.UtcNow;

    try
    {
      var removed = await outages.DeleteClosedOlderThanAsync(DateTime.UtcNow - OutageRetention, ct);
      if (removed > 0)
        logger.LogInformation("Removed {Count} source outage row(s) older than {Days} days",
          removed, OutageRetention.TotalDays);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Source outage cleanup failed");
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken)
  {
    await _cts.CancelAsync();
    if (_task != null)
    {
      try { await _task; } catch (OperationCanceledException) { }
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync(CancellationToken.None);
    _cts.Dispose();
  }
}
