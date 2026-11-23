using System.Collections.Generic;
using System.Linq;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  /// <summary>What the video channel says about one camera's streams.</summary>
  internal sealed record CameraVideoSummary(
    bool HasConfiguredStreams,
    bool AnyAlive,
    bool AnyNoVideo,
    bool AllNoVideo);

  /// <summary>
  /// The presence states to write; null means "leave this one as it is".
  /// </summary>
  internal readonly record struct PresenceDecision(bool? Connected, bool? NoVideo);

  // The two channels meet here and nowhere else: the video state says whether frames arrive, the
  // ONVIF probe says whether the camera answers at all, and the camera's node carries the two
  // independent results. Kept free of the database and of gRPC so the whole table is testable.
  internal static class CameraPresenceRules
  {
    /// <summary>Video is present only when a configured stream is delivering frames.</summary>
    public static CameraVideoSummary Summarize(
      CameraInfo camera, IReadOnlyDictionary<string, VideoChannelState>? states)
    {
      if (camera.VideoStreams is { } configured)
      {
        var anyAlive = configured.Any(s => StateOf(states, s) == VideoChannelState.Alive);
        var anyNoVideo = configured.Any(s => StateOf(states, s) == VideoChannelState.NoVideo);
        return new CameraVideoSummary(
          HasConfiguredStreams: configured.Count > 0,
          AnyAlive: anyAlive,
          AnyNoVideo: anyNoVideo,
          // Every configured stream has to be known dead: one dead stream beside one that was
          // never heard is not proof that the camera shows nothing.
          AllNoVideo: configured.Count > 0
            && configured.All(s => StateOf(states, s) == VideoChannelState.NoVideo));
      }

      // An older caller without the stream list: judge by what was actually heard.
      var known = states?.Values.ToList() ?? [];
      return new CameraVideoSummary(
        HasConfiguredStreams: known.Count > 0,
        AnyAlive: known.Any(s => s == VideoChannelState.Alive),
        AnyNoVideo: known.Any(s => s == VideoChannelState.NoVideo),
        AllNoVideo: known.Count > 0 && known.All(s => s == VideoChannelState.NoVideo));
    }

    /// <summary>
    /// Ask the camera only where the video channel gives a reason to and ONVIF could answer: no
    /// live stream, and either a known dead stream or no streams at all. Streams that are merely
    /// not heard from yet are not a reason — otherwise a dead media server would turn into a probe
    /// of every camera in the deployment.
    /// </summary>
    public static bool ShouldProbe(CameraInfo camera, CameraVideoSummary video) =>
      camera.HasOnvif
      && !video.AnyAlive
      && (video.AnyNoVideo || !video.HasConfiguredStreams);

    public static PresenceDecision Decide(CameraVideoSummary video, CameraReachability reachability)
    {
      if (video.AnyAlive)
        return new PresenceDecision(Connected: true, NoVideo: false);

      if (video.AllNoVideo)
      {
        // The video channel is wholly negative. If the camera answers, it is reachable and has no
        // video; if it does not, it is gone. No ONVIF configured means video is the only channel.
        return reachability switch
        {
          CameraReachability.Reachable => new PresenceDecision(Connected: true, NoVideo: true),
          CameraReachability.Unreachable => new PresenceDecision(Connected: false, NoVideo: true),
          CameraReachability.NoOnvif => new PresenceDecision(Connected: false, NoVideo: true),
          // Could not ask: the connection keeps its last value, the video verdict still stands.
          _ => new PresenceDecision(Connected: null, NoVideo: true),
        };
      }

      if (video.AnyNoVideo)
      {
        // Some streams are dead, some are not heard from: not enough for NO_VIDEO. A camera that
        // answers is connected; a camera that does not is gone; with no other channel there is
        // nothing to conclude.
        return reachability switch
        {
          CameraReachability.Reachable => new PresenceDecision(Connected: true, NoVideo: false),
          CameraReachability.Unreachable => new PresenceDecision(Connected: false, NoVideo: false),
          _ => new PresenceDecision(Connected: null, NoVideo: null),
        };
      }

      if (!video.HasConfiguredStreams)
      {
        // ONVIF is the camera's only channel. A failed probe says nothing; a missing one says
        // nothing either — there is no evidence to act on.
        return reachability switch
        {
          CameraReachability.Reachable => new PresenceDecision(Connected: true, NoVideo: false),
          CameraReachability.Unreachable => new PresenceDecision(Connected: false, NoVideo: false),
          _ => new PresenceDecision(Connected: null, NoVideo: null),
        };
      }

      // Nothing is known about the streams: "not observed" never becomes "not there".
      return new PresenceDecision(Connected: null, NoVideo: null);
    }

    private static VideoChannelState StateOf(
      IReadOnlyDictionary<string, VideoChannelState>? states, string stream) =>
      states is not null && states.TryGetValue(stream, out var state)
        ? state
        : VideoChannelState.Unknown;
  }
}
