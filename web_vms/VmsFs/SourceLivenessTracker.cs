using System.Collections.Concurrent;

namespace VmsFs;

/// <summary>
/// Turns per-stream heartbeats from media_server into outage intervals.
/// </summary>
/// <remarks>
/// Current state of live streams is held in memory on purpose; only intervals reach the database.
/// "No signal" then means one thing whether the camera died or the media server did, and nothing can
/// be left half-open by a crash — the open rows themselves are the state that survives a restart.
///
/// Boundaries come from the frames themselves, never from when a report arrived: an interval starts
/// at the last packet received before the silence and ends at the first one after it. Otherwise both
/// ends would be up to one heartbeat period out, and a backfill would re-pull footage the archive
/// already has.
///
/// Only streams that were supposed to be recording produce a row. With recording off — no schedule
/// window, no arming, no alarm — an outage costs no footage, so there is nothing to restore and
/// nothing worth remembering.
/// </remarks>
public sealed class SourceLivenessTracker(
  SourceOutageStore outages,
  ClipStorage clips,
  ILogger<SourceLivenessTracker> logger)
{
  /// <summary>An outage shorter than this is a blink and is never materialised.</summary>
  public static readonly TimeSpan Grace = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_SOURCE_OUTAGE_GRACE_SECONDS"), out var g) && g > 0
      ? g : 15);

  /// <summary>Silence longer than this means the source is gone, however it went.</summary>
  public static readonly TimeSpan Stale = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("VMS_SOURCE_LIVENESS_STALE_SECONDS"), out var s) && s > 0
      ? s : 60);

  private sealed record StreamState(
    DateTime LastReportUtc,
    DateTime? LastFrameUtc,
    bool RecordingExpected,
    bool Alive,
    DateTime? PendingSinceUtc,
    string InstanceId);

  private readonly ConcurrentDictionary<(string Camera, string Stream), StreamState> _state = new();
  private readonly ConcurrentDictionary<(string Camera, string Stream), byte> _openOutages = new();

  public static (string Camera, string Stream) Key(string cameraId, string streamName) =>
    (cameraId, streamName ?? string.Empty);

  /// <summary>Rebuilds "which streams are silent" from the open rows after a restart.</summary>
  public async Task LoadOpenAsync(CancellationToken ct)
  {
    foreach (var (camera, stream) in await outages.ListOpenAsync(ct))
      _openOutages[Key(camera, stream)] = 0;
  }

  /// <summary>Applies one heartbeat. Called from the gRPC handler.</summary>
  public async Task ApplyAsync(
    string cameraId, string streamName, bool alive, DateTime? lastFrameUtc,
    DateTime? recoveredFrameUtc, bool recordingExpected, string instanceId, CancellationToken ct)
  {
    var key = Key(cameraId, streamName);
    var now = DateTime.UtcNow;
    var previous = _state.TryGetValue(key, out var p) ? p : null;
    // A restart is also a recovery: both branches below would otherwise write the same hole, from
    // slightly different boundaries, as two rows.
    var recordedHere = false;

    // A media server that restarted between two heartbeats never looked silent, so nothing above
    // would ever notice — while the archive stopped for as long as the process was away. The report
    // carries which run it comes from, and a change of that is exactly the missing stretch: from
    // the last frame the previous run saw to the first frame this one has.
    if (previous is not null
        && !string.IsNullOrEmpty(instanceId)
        && !string.IsNullOrEmpty(previous.InstanceId)
        && previous.InstanceId != instanceId
        && previous.LastFrameUtc is { } lastFrameFromHeartbeat)
    {
      // Starting points, refined from the archive just below.
      var restoredAt = recoveredFrameUtc ?? lastFrameUtc ?? now;
      var lastSeen = lastFrameFromHeartbeat;

      // The clip the dead run was writing is still marked as open, and an open clip is read as
      // covering the next minute — which is precisely the stretch that has just been lost. Closed
      // first, or the gap below would be judged already filled and nothing would be recovered.
      var (healed, healedFrom, healedTo) =
        await clips.FinalizeDanglingClipsAsync(cameraId, streamName, lastSeen, ct);
      if (healed > 0)
      {
        // Gaps closed on the strength of the old, larger assumption are no longer settled.
        var reopened = await outages.ReopenFilledAsync(cameraId, streamName, healedFrom, healedTo, ct);
        logger.LogInformation(
          "Measured {Count} clip(s) left open by the previous run of {CameraId}/{StreamName}; "
            + "reopened {Reopened} gap(s) judged against them",
          healed, cameraId, streamName, reopened);
      }

      // Both boundaries come from the archive, not from the heartbeats. A process that died between
      // two reports left the heartbeats unable to say anything precise, while the clips are exact:
      // recording stopped where the interrupted one ends — now measurable, since it was just
      // finished from its own fragments — and resumed where the next one begins. A frame received
      // in between is not footage: nothing is written until a keyframe arrives.
      var lastClip = await outages.LastClipEndAsync(cameraId, streamName, lastSeen, ct);
      if (lastClip is { } clipEnd && clipEnd < restoredAt)
        lastSeen = clipEnd;

      var nextClip = await outages.FirstClipStartAfterAsync(cameraId, streamName, lastSeen, ct);
      if (nextClip is { } resumedAt && resumedAt > lastSeen)
        restoredAt = resumedAt;

      // Only when it cost footage, and only when it is long enough to be a hole rather than the
      // rounding between two frames.
      if (previous.RecordingExpected
          && restoredAt - lastSeen >= TimeSpan.FromSeconds(1)
          && !_openOutages.ContainsKey(key))
      {
        logger.LogInformation(
          "Media server restart detected for {CameraId}/{StreamName}: archive gap {From:o}..{To:o}",
          cameraId, streamName, lastSeen, restoredAt);
        await OpenAsync(cameraId, streamName, lastSeen, cause: "server_down", ct);
        // Bounded on both sides already, so it is closed straight away rather than waiting for a
        // recovery report that has, in effect, already happened.
        if (_openOutages.TryRemove(key, out _))
          await outages.CloseAsync(cameraId, streamName, restoredAt, ct);
        recordedHere = true;
      }
    }

    if (alive)
    {
      // A short outage can end before anything above notices it: the reader needs its watchdog to
      // declare the source dead, that verdict waits for the next heartbeat, and the grace window
      // waits again — some thirty-five seconds in all, while the recovery report may arrive first.
      // It carries both boundaries, though, so the interval is written here from the frame before
      // the silence to the frame after it. Measured on a real cable pull: 21 s of missing archive
      // that no other path recorded.
      if (!recordedHere
          && previous is { Alive: false, RecordingExpected: true, LastFrameUtc: { } silentFrom }
          && !_openOutages.ContainsKey(key))
      {
        var silentUntil = recoveredFrameUtc ?? lastFrameUtc ?? now;
        if (silentUntil - silentFrom >= Grace)
        {
          logger.LogInformation(
            "Short outage recovered for {CameraId}/{StreamName}: {From:o}..{To:o}",
            cameraId, streamName, silentFrom, silentUntil);
          await OpenAsync(cameraId, streamName, silentFrom, cause: "link_lost", ct);
          if (_openOutages.TryRemove(key, out _))
            await outages.CloseAsync(cameraId, streamName, silentUntil, ct);
        }
      }

      _state[key] = new StreamState(now, lastFrameUtc, recordingExpected, true, null, instanceId);
      // Closed on the FIRST frame after the pause, which media_server reports separately.
      // last_frame_ts would not do: it advances with every packet, so by the time this report was
      // assembled it points at the newest one, and the gap would be recorded up to a full heartbeat
      // period longer than it was — then a backfill would re-pull footage the archive already has.
      if (_openOutages.TryRemove(key, out _))
        await outages.CloseAsync(cameraId, streamName, recoveredFrameUtc ?? lastFrameUtc ?? now, ct);
      return;
    }

    // Dead.
    var pendingSince = previous?.PendingSinceUtc ?? now;
    var lastFrame = lastFrameUtc ?? previous?.LastFrameUtc;
    _state[key] = new StreamState(now, lastFrame, recordingExpected, false, pendingSince, instanceId);

    // Held back until the grace window passes, so a blink never reaches the table.
    if (now - pendingSince < Grace || _openOutages.ContainsKey(key))
      return;

    // The flag as media_server reports it now, not as it was when the source died: it comes from
    // the stream's recording policy, which the media server still knows while the camera is down.
    // So a camera that goes offline unrecorded and is armed later does start producing a row.
    if (!recordingExpected)
      return;

    // Resolved only once the interval is actually going to be written: while the source stays dead
    // this runs on every heartbeat, and the fallback is a query over clips.
    // The interval starts at the last frame we ever saw, never at now.
    var startedAt = lastFrame ?? await FallbackStartAsync(cameraId, streamName, ct);

    await OpenAsync(cameraId, streamName, startedAt, cause: "link_lost", ct);
  }

  /// <summary>
  /// Opens an interval for a stream that has stopped reporting altogether.
  /// </summary>
  /// <remarks>
  /// The caller decides whether the stream was supposed to be recording — from configuration, since
  /// after a node-wide restart no heartbeat ever reached this process, and that restart is exactly
  /// the case the whole feature exists for.
  /// </remarks>
  public async Task OpenForSilenceAsync(
    string cameraId, string streamName, string cause, CancellationToken ct)
  {
    var key = Key(cameraId, streamName);
    if (_openOutages.ContainsKey(key))
      return;

    var previous = _state.TryGetValue(key, out var p) ? p : null;
    var startedAt = previous?.LastFrameUtc ?? await FallbackStartAsync(cameraId, streamName, ct);
    await OpenAsync(cameraId, streamName, startedAt, cause, ct);
  }

  /// <summary>Streams heard from within <see cref="Stale"/>, so the sweeper can skip them.</summary>
  public bool IsFresh((string Camera, string Stream) key, DateTime nowUtc) =>
    _state.TryGetValue(key, out var s) && nowUtc - s.LastReportUtc < Stale;

  public bool HasOpenOutage((string Camera, string Stream) key) => _openOutages.ContainsKey(key);

  /// <summary>
  /// Drops in-memory state for streams that are no longer configured. Their open intervals are
  /// closed separately by the sweeper; this only stops the map growing for every stream ever seen.
  /// </summary>
  public void PruneTo(IReadOnlySet<(string Camera, string Stream)> expected)
  {
    foreach (var key in _state.Keys)
      if (!expected.Contains(key))
        _state.TryRemove(key, out _);
  }

  /// <summary>Forgets a stream that left the configuration and closes its interval if any.</summary>
  public async Task ForgetAsync(string cameraId, string streamName, CancellationToken ct)
  {
    var key = Key(cameraId, streamName);
    _state.TryRemove(key, out _);
    if (_openOutages.TryRemove(key, out _))
      await outages.CloseAsync(cameraId, streamName, DateTime.UtcNow, ct);
  }

  private async Task OpenAsync(
    string cameraId, string streamName, DateTime startedAt, string cause, CancellationToken ct)
  {
    try
    {
      // The archive is the last word on when recording actually stopped. A start earlier than the
      // final clip's end would claim a hole over footage that is right there — and now that a gap
      // is only closed by recovered material, that stretch would be pulled from the camera again.
      var lastClip = await outages.LastClipEndAsync(cameraId, streamName, startedAt, ct);
      if (lastClip is { } clipEnd && clipEnd > startedAt && clipEnd < DateTime.UtcNow)
      {
        startedAt = clipEnd;
      }

      await outages.OpenAsync(cameraId, streamName, startedAt, cause, Grace, ct);
      _openOutages[Key(cameraId, streamName)] = 0;
      logger.LogInformation(
        "Source outage opened for {CameraId}/{StreamName} at {StartedAt:o} ({Cause})",
        cameraId, streamName, startedAt, cause);
    }
    catch (Exception ex)
    {
      // Not fatal: the next tick sees the same silence and tries again, and the boundary is taken
      // from the frame timestamp rather than from when the write happened to succeed.
      logger.LogWarning(ex, "Could not open source outage for {CameraId}/{StreamName}", cameraId, streamName);
    }
  }

  /// <summary>
  /// Where an interval starts when no in-memory frame timestamp survived — i.e. after the whole node
  /// went down. The last clip's end is not an estimate: the archive stopped exactly there.
  /// </summary>
  private async Task<DateTime> FallbackStartAsync(string cameraId, string streamName, CancellationToken ct)
  {
    // Without a frame timestamp in memory the only defensible claim is "this stream has been quiet
    // for at least as long as it takes to notice". The archive's last clip refines that — but only
    // within that window: reaching further back would invent a gap over a period when nothing was
    // supposed to be recorded at all, which is exactly what happens when recording is switched on
    // after the server spent an hour idle.
    var now = DateTime.UtcNow;
    var earliest = now - Stale;
    var lastClip = await outages.LastClipEndAsync(cameraId, streamName, now, ct);
    return lastClip is { } clipEnd && clipEnd > earliest ? clipEnd : earliest;
  }
}
