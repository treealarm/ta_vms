using System.Collections.Concurrent;

namespace VmsFs;

/// <summary>
/// Turns per-stream heartbeats from video_srv into outage intervals.
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
      //
      // Bounded by this run's first frame, not by the last heartbeat: everything the dead run wrote
      // began before that frame, while its own segment cannot have started earlier. The heartbeat
      // is the wrong bound — a segment opened in the seconds between the last report and the crash
      // would be skipped and stay unfinished for good, which with short pre-alarm segments is not
      // the exception but the usual case.
      var (healed, _, healedFrom, healedTo) =
        await clips.FinalizeDanglingClipsAsync(cameraId, streamName, restoredAt, ct);
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
      // Same bound, for the same reason: asking only about clips older than the last heartbeat
      // would ignore the one that was just measured and hand back a boundary from before the crash.
      var lastClip = await outages.LastClipEndAsync(cameraId, streamName, restoredAt, ct);
      if (lastClip is { } clipEnd && clipEnd < restoredAt)
        lastSeen = clipEnd;

      var nextClip = await outages.FirstClipStartAfterAsync(cameraId, streamName, lastSeen, ct);
      if (nextClip is { } resumedAt && resumedAt > lastSeen)
        restoredAt = resumedAt;

      // Only when it cost footage, and only when it is long enough to be a hole rather than the
      // rounding between two frames.
      if (previous.RecordingExpected && restoredAt - lastSeen >= TimeSpan.FromSeconds(1))
      {
        if (!_openOutages.ContainsKey(key))
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
        else
        {
          // The sweeper noticed the silence first and opened the row from the last heartbeat, which
          // predates the last written frame by up to one heartbeat period. The interrupted clip has
          // just been measured, so the boundary can now be taken from the archive instead of a guess.
          var moved = await outages.AdvanceOpenStartAsync(cameraId, streamName, lastSeen, ct);
          if (moved > 0)
            logger.LogInformation(
              "Gap start for {CameraId}/{StreamName} corrected to {From:o} — where the clip left open "
                + "by the previous run actually ends", cameraId, streamName, lastSeen);
        }
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
      // Closed on the FIRST frame after the pause, which video_srv reports separately.
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

    // The flag as video_srv reports it now, not as it was when the source died: it comes from
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

    // Silence was judged on a list the sweeper built at the start of its pass, and a media server
    // coming back reports the moment its first frame arrives. Between those two instants the stream
    // can go from silent to alive, and writing then records an outage on a source that is demonstrably
    // running — one that the very next report closes microseconds later, leaving a row saying the
    // server was down for no time at all.
    if (IsFresh(key, DateTime.UtcNow))
      return;

    // The boundary is about to be read off the archive, and an unfinished clip lies about it: every
    // query treats a null end as no time at all, so the last clip of the dead run reads as ending
    // where it began and the hole is claimed over footage that exists. Measure it first — and if it
    // cannot be measured yet, wait rather than guess. Nothing is lost by waiting: the boundary comes
    // from the frames, not from the moment we noticed.
    var (_, deferred, _, _) =
      await clips.FinalizeDanglingClipsAsync(cameraId, streamName, DateTime.UtcNow, ct);
    if (deferred > 0)
      return;

    var previous = _state.TryGetValue(key, out var p) ? p : null;
    var startedAt = previous?.LastFrameUtc ?? await FallbackStartAsync(cameraId, streamName, ct);
    await OpenAsync(cameraId, streamName, startedAt, cause, ct);
  }

  /// <summary>
  /// Recovers the holes a node-wide restart leaves behind, reading them off the archive.
  /// </summary>
  /// <remarks>
  /// The heartbeat path cannot see this case even in principle. It recognises a restart by the
  /// instance id changing between two reports, and when the whole node goes down VmsFs goes with it:
  /// there is no earlier report left to compare against, so no restart is ever detected and the clip
  /// the writer had open stays unfinished — read, as an open clip is, as covering the minute after it
  /// starts, which is exactly the minute that was lost.
  ///
  /// What survives is the archive. A clip with no end and a later clip behind it was abandoned; its
  /// real length is in its own fragments, and from there to the next clip nothing was recorded. That
  /// the clip exists at all is itself the proof that recording was expected — nothing else opens a
  /// segment — so no configuration needs to be consulted.
  /// </remarks>
  public async Task RecoverAbandonedAsync(
    IReadOnlyCollection<string> cameraIds,
    IReadOnlySet<(string Camera, string Stream)> recording,
    CancellationToken ct)
  {
    List<AbandonedClip> abandoned;
    try
    {
      abandoned = await clips.ListAbandonedAsync(cameraIds, ct);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not look for clips left open by a previous run");
      return;
    }

    foreach (var clip in abandoned)
    {
      if (ct.IsCancellationRequested)
        return;
      try
      {
        // Without a successor the hole has no end yet, so the archive is read up to now.
        var boundary = clip.ResumedAt ?? DateTime.UtcNow;
        var (healed, _, _, _) = await clips.FinalizeDanglingClipsAsync(
          clip.CameraId, clip.StreamName, boundary, ct);
        if (healed == 0)
          continue;

        // Re-read rather than trust the measurement: the same pass may have finished several clips,
        // and the gap starts at the end of the last of them.
        var stoppedAt = await outages.LastClipEndAsync(
          clip.CameraId, clip.StreamName, boundary, ct);
        if (stoppedAt is not { } lastFrame)
          continue;

        // An outage still open for this stream is the present, not the past — leave it to the
        // heartbeat path, which owns the live interval and will close it on the first frame back.
        var key = Key(clip.CameraId, clip.StreamName);
        if (_openOutages.ContainsKey(key))
          continue;

        if (clip.ResumedAt is { } resumedAt)
        {
          if (resumedAt - lastFrame < TimeSpan.FromSeconds(1))
            continue;
          var written = await outages.RecordRecoveredAsync(
            clip.CameraId, clip.StreamName, lastFrame, resumedAt, "server_down", ct);
          if (written)
            logger.LogInformation(
              "Archive gap recovered from the clips of {CameraId}/{StreamName}: {From:o}..{To:o} — "
                + "the previous run left a clip unfinished and nothing observed the restart",
              clip.CameraId, clip.StreamName, lastFrame, resumedAt);
          continue;
        }

        // Nothing has been recorded since, so the hole is still open. Only worth a row if the stream
        // is supposed to be recording right now — otherwise the archive simply ends there because
        // somebody switched recording off, which is not an outage.
        if (!recording.Contains(key))
          continue;

        await OpenAsync(clip.CameraId, clip.StreamName, lastFrame, cause: "server_down", ct);
        logger.LogInformation(
          "Archive gap opened from the clips of {CameraId}/{StreamName} at {From:o} — a clip was "
            + "left unfinished and recording has not resumed",
          clip.CameraId, clip.StreamName, lastFrame);
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Could not recover the gap around the clip {CameraId}/{StreamName} at {At:o}",
          clip.CameraId, clip.StreamName, clip.StartTs);
      }
    }
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
