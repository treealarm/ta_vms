using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Cfg;

namespace VmsSingleton;

/// Watches recording-schedule window boundaries and notifies downstream services when a camera's
/// effective recording state flips.
///
/// Holds no authoritative state: schedule_active is always recomputed from the wall clock by whoever
/// reads a CameraSetup. This loop only exists so that nobody has to poll — at a boundary VmsCfg
/// publishes the existing server-config-changed event, media_server
/// reloads and applies the new policy, and VmsFs drops its caches.
///
/// It lives here rather than in VmsCfg because VmsCfg is replicated: every replica would keep its own
/// _lastActive, spot the same boundary, and announce it, turning one window edge into N full config
/// reloads on the node. Evaluation itself stays in VmsCfg — this asks for the resolved state, so
/// there is no second copy of the schedule rules to drift.
public sealed class RecordingScheduleService(
  CfgService.CfgServiceClient cfgClient,
  ScheduleChangeSignal changeSignal,
  ILogger<RecordingScheduleService> logger) : BackgroundService
{
  /// Upper bound on a single sleep. A wrong boundary computation then costs at most this much
  /// lateness instead of stalling the schedule until the next edit or restart.
  private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(1);
  /// Used instead of the next boundary when a notification failed: the boundary that needs
  /// re-announcing is already behind us, and the one ahead may be days away.
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(10);

  // Last known active/inactive state per camera. Empty on startup, so the first pass notifies every
  // scheduled camera — idempotent, and it re-syncs anything that changed while we were down.
  private readonly Dictionary<string, bool> _lastActive = [];

  // Set when a boundary could not be announced, so the next wake-up comes soon rather than at the
  // next scheduled boundary.
  private bool _retryPending;

  private DateTime? _nextTransitionUtc;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await TickAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "RecordingScheduleService tick failed");
      }

      await changeSignal.WaitForChangeAsync(NextSleep(), stoppingToken);
    }
  }

  private async Task TickAsync(CancellationToken ct)
  {
    var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
    var state = await cfgClient.GetRecordingScheduleStateAsync(new Empty(), opts);

    _nextTransitionUtc = state.NextTransition?.ToDateTime();

    var active = new HashSet<string>(state.ActiveCameraIds, StringComparer.Ordinal);
    var transitions = new List<string>();

    foreach (var cameraId in state.ScheduledCameraIds)
    {
      var isActive = active.Contains(cameraId);
      if (_lastActive.TryGetValue(cameraId, out var previous) && previous == isActive)
        continue;

      transitions.Add(cameraId);
      logger.LogInformation(
        "Recording schedule window {State} for camera '{CameraId}'", isActive ? "opened" : "closed", cameraId);
    }

    // Cameras that lost their last schedule assignment: forget them so a re-assignment is treated
    // as a fresh transition.
    var scheduled = new HashSet<string>(state.ScheduledCameraIds, StringComparer.Ordinal);
    foreach (var cameraId in _lastActive.Keys.Where(id => !scheduled.Contains(id)).ToList())
      _lastActive.Remove(cameraId);

    if (transitions.Count == 0)
      return;

    // The new state is recorded only once the notification is out. media_server learns about a
    // boundary from that event alone, so treating a failed publish as done would lose the whole
    // window; leaving _lastActive untouched makes the next tick retry.
    var request = new NotifyCamerasChangedRequest();
    request.CameraIds.AddRange(transitions);
    var status = await cfgClient.NotifyCamerasChangedAsync(
      request, new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct));

    if (!status.Success)
    {
      _retryPending = true;
      logger.LogWarning("Recording schedule notification failed, retrying shortly");
      return;
    }

    _retryPending = false;
    foreach (var cameraId in transitions)
      _lastActive[cameraId] = active.Contains(cameraId);
  }

  private TimeSpan NextSleep()
  {
    if (_retryPending)
      return RetryDelay;

    if (_nextTransitionUtc is null)
      return MaxSleep;

    var delay = _nextTransitionUtc.Value - DateTime.UtcNow;
    if (delay < MinSleep) return MinSleep;
    if (delay > MaxSleep) return MaxSleep;
    return delay;
  }
}
