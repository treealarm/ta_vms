using System;
using System.Collections.Generic;
using System.Threading.Channels;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Watch
{
  // One open watch stream: what it follows, for whom, and the diffs already sent. Commands change
  // it; a generation counter lets a poll that was already reading discard its answer when the
  // command changed underneath it.
  internal sealed class WatchSubscription : IDisposable
  {
    private readonly object _sync = new();

    public Guid Id { get; } = Guid.NewGuid();

    public Channel<MapProto.WatchMessage> Messages { get; } =
      Channel.CreateBounded<MapProto.WatchMessage>(new BoundedChannelOptions(1000)
      {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
      });

    public sealed record Snapshot(
      CallerContext Caller,
      BoxDTO? Box,
      IReadOnlyList<string> Ids,
      bool UpdateValues,
      long Generation);

    private CallerContext _caller = CallerContext.Anonymous;
    private BoxDTO? _box;
    private IReadOnlyList<string> _ids = [];
    private bool _updateValues;
    private long _generation;

    // Written by the poller under TryApply; a command resets them under the same lock.
    public Dictionary<string, uint> GeoVersions { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SentGeo { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, uint> StateVersions { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, uint> AlarmVersions { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> SentIds { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, uint> ValueVersions { get; set; } = new(StringComparer.Ordinal);

    public Snapshot GetSnapshot()
    {
      lock (_sync)
        return new Snapshot(_caller, _box, _ids, _updateValues, _generation);
    }

    public void SetCaller(CallerContext caller)
    {
      lock (_sync)
      {
        _caller = caller;
        _generation++;
      }
    }

    public void SetBox(BoxDTO box, CallerContext caller)
    {
      lock (_sync)
      {
        _caller = caller;
        _box = box;
        _ids = [];
        _updateValues = false;
        ResetDiff();
        _generation++;
      }
    }

    public void SetIds(IReadOnlyList<string> ids, CallerContext caller)
    {
      lock (_sync)
      {
        _caller = caller;
        _box = null;
        _ids = ids;
        ResetDiff();
        _generation++;
      }
    }

    public void SetValues(bool enabled, CallerContext caller)
    {
      lock (_sync)
      {
        _caller = caller;
        _updateValues = enabled;
        ValueVersions.Clear();
        _generation++;
      }
    }

    // The poller applies a diff only if no command landed while it was reading.
    public bool TryApply(long generation, Action apply)
    {
      lock (_sync)
      {
        if (_generation != generation)
          return false;

        apply();
        return true;
      }
    }

    public void Send(MapProto.WatchMessage message) => Messages.Writer.TryWrite(message);

    private void ResetDiff()
    {
      GeoVersions.Clear();
      SentGeo.Clear();
      StateVersions.Clear();
      AlarmVersions.Clear();
      SentIds.Clear();
      ValueVersions.Clear();
    }

    public void Dispose()
    {
      Messages.Writer.TryComplete();
      SentGeo.Clear();
    }
  }
}
