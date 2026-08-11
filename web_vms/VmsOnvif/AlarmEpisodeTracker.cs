using System.Collections.Concurrent;

namespace VmsOnvif;

/// <summary>
/// Collapses a stream of repeating ONVIF events into alarm episodes, so only a state change costs
/// anything.
/// </summary>
/// <remarks>
/// A motion detector re-announces itself every second or two for as long as it sees movement. Every
/// one of those used to open its own retention episode and run a gRPC chain across three services —
/// for a condition that had not changed since the previous announcement. An event is a notification;
/// the work belongs on the edges of the condition it describes, not on its repetitions.
///
/// So a repeat only extends the episode in memory here, and the retention call goes out again just
/// often enough to keep the archive's window ahead of the alarm (half its timeout), reusing the same
/// event id so VmsFs extends the episode it already has instead of starting another.
///
/// In-memory and per node, like everything else in this service: an episode that is lost on restart
/// simply becomes a new one, and the worst case is one extra retention call.
/// </remarks>
public sealed class AlarmEpisodeTracker
{
  /// <summary>What to do about one matched event. <c>Send</c> false means it changed nothing.</summary>
  public readonly record struct Decision(bool Send, string EventId, bool IsNewEpisode);

  private sealed record Episode(string EventId, DateTime LastSentAt, DateTime ExpiresAt);

  private readonly ConcurrentDictionary<string, Episode> _episodes = new();

  // An episode nobody has renewed for well past its timeout is over; sweeping on use keeps the map
  // proportional to what is actually alarming rather than to everything that ever has.
  private static readonly TimeSpan SweepAfter = TimeSpan.FromMinutes(10);
  private DateTime _lastSweep = DateTime.MinValue;

  /// <summary>
  /// Decides whether a matched event needs a retention call. <paramref name="timeout"/> is the
  /// alarm filter's own window.
  /// </summary>
  public Decision Evaluate(string key, TimeSpan timeout, DateTime now)
  {
    Sweep(now);

    var renewAfter = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, timeout.Ticks / 2));

    while (true)
    {
      if (_episodes.TryGetValue(key, out var current) && current.ExpiresAt > now)
      {
        // The condition is still the same one. Renew the archive's window only when it is close to
        // running out; otherwise this repeat costs nothing at all.
        var due = now - current.LastSentAt >= renewAfter;
        var updated = current with
        {
          LastSentAt = due ? now : current.LastSentAt,
          ExpiresAt = now + timeout,
        };
        if (!_episodes.TryUpdate(key, updated, current))
          continue; // another event for the same key raced us; re-read and decide again

        return new Decision(due, current.EventId, IsNewEpisode: false);
      }

      // No live episode: this event is the start of one.
      var episode = new Episode($"onvif-{Guid.NewGuid()}", now, now + timeout);
      if (current is null)
      {
        if (!_episodes.TryAdd(key, episode))
          continue;
      }
      else if (!_episodes.TryUpdate(key, episode, current))
      {
        continue;
      }

      return new Decision(true, episode.EventId, IsNewEpisode: true);
    }
  }

  /// <summary>
  /// Ends the episode because the camera said the condition cleared. The next start is then a new
  /// episode rather than a continuation of one the camera has already closed.
  /// </summary>
  public void Clear(string key) => _episodes.TryRemove(key, out _);

  private void Sweep(DateTime now)
  {
    if (now - _lastSweep < SweepAfter)
      return;
    _lastSweep = now;

    foreach (var (key, episode) in _episodes)
    {
      if (now - episode.ExpiresAt > SweepAfter)
        _episodes.TryRemove(key, out _);
    }
  }
}
