using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Watch
{
  // One poll per process for every open watch stream. Square polled once per stream and re-read
  // the whole observed set every second; the streams here share the reads, and the diff is by the
  // database's xmin, so a message means a row really changed rather than "the poll ran".
  public sealed class MapWatchPoller : BackgroundService
  {
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Guid, WatchSubscription> _subscriptions = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MapWatchPoller> _logger;

    public MapWatchPoller(
      IServiceScopeFactory scopes, ILogger<MapWatchPoller> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal WatchSubscription Subscribe()
    {
      var subscription = new WatchSubscription();
      _subscriptions[subscription.Id] = subscription;
      return subscription;
    }

    internal void Unsubscribe(WatchSubscription subscription)
    {
      _subscriptions.TryRemove(subscription.Id, out _);
      subscription.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      using var timer = new PeriodicTimer(Interval);

      while (true)
      {
        try
        {
          if (!await timer.WaitForNextTickAsync(stoppingToken))
            return;
        }
        catch (OperationCanceledException)
        {
          return;
        }

        using var scope = _scopes.CreateScope();
        var snapshot = scope.ServiceProvider.GetRequiredService<WatchSnapshotService>();

        foreach (var subscription in _subscriptions.Values)
        {
          try
          {
            await PollAsync(subscription, snapshot, stoppingToken);
          }
          catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
          {
            return;
          }
          catch (Exception ex)
          {
            _logger.LogWarning(ex, "Map watch poll failed for subscription {Id}", subscription.Id);
          }
        }
      }
    }

    internal static async Task PollAsync(
      WatchSubscription subscription, WatchSnapshotService snapshot, CancellationToken ct)
    {
      var state = subscription.GetSnapshot();

      if (state.Box is not null)
        await PollBoxAsync(subscription, snapshot, state, ct);
      else if (state.Ids.Count > 0)
        await PollIdsAsync(subscription, snapshot, state, ct);
    }

    private static async Task PollBoxAsync(
      WatchSubscription subscription,
      WatchSnapshotService snapshot,
      WatchSubscription.Snapshot state,
      CancellationToken ct)
    {
      var geo = await snapshot.GeoAsync(state.Box!, ct);
      var visible = await snapshot.VisibleAsync(geo.Keys.ToList(), state.Caller, ct);

      var updates = visible
        .Where(id => !subscription.GeoVersions.TryGetValue(id, out var version)
                     || version != geo[id].Version)
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();
      var deletes = subscription.SentGeo
        .Where(id => !visible.Contains(id))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();

      var cameras = await snapshot.CameraIdsAsync(updates, ct);

      var applied = subscription.TryApply(state.Generation, () =>
      {
        subscription.GeoVersions = geo.ToDictionary(
          kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal);
        subscription.SentGeo = visible;
      });
      if (!applied)
        return;

      if (updates.Count > 0)
      {
        Send(subscription, "set_ids2update",
          updates.Select(id => Item(id, cameras.GetValueOrDefault(id))));
      }

      if (deletes.Count > 0)
        Send(subscription, "set_ids2delete", deletes.Select(id => Item(id, null)));
    }

    private static async Task PollIdsAsync(
      WatchSubscription subscription,
      WatchSnapshotService snapshot,
      WatchSubscription.Snapshot state,
      CancellationToken ct)
    {
      var ids = state.Ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.Ordinal)
        .ToList();

      var visible = await snapshot.VisibleAsync(ids, state.Caller, ct);
      var states = await snapshot.StatesAsync(visible, ct);
      var alarms = await snapshot.AlarmsAsync(visible, ct);

      var changed = visible
        .Where(id => StateVersion(states, id) != subscription.StateVersions.GetValueOrDefault(id)
                     || AlarmVersion(alarms, id) != subscription.AlarmVersions.GetValueOrDefault(id))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();
      var lost = subscription.SentIds
        .Where(id => !visible.Contains(id))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();

      var cameras = await snapshot.CameraIdsAsync(changed, ct);

      var valueChanged = new List<string>();
      var valueCameras = new Dictionary<string, string>(StringComparer.Ordinal);
      Dictionary<string, ValueDTO> values = new(StringComparer.Ordinal);

      if (state.UpdateValues)
      {
        values = await snapshot.ValuesAsync(visible, ct);
        var owners = await snapshot.ValueOwnersAsync(visible, ct);

        var changedOwners = values.Values
          .Where(v => !subscription.ValueVersions.TryGetValue(v.id ?? string.Empty, out var version)
                      || version != v.Version)
          .Select(v => v.owner_id)
          .Where(id => !string.IsNullOrEmpty(id))
          .ToHashSet(StringComparer.Ordinal);

        valueChanged = visible
          .Where(id => changedOwners.Contains(owners.GetValueOrDefault(id, id)))
          .OrderBy(id => id, StringComparer.Ordinal)
          .ToList();

        if (valueChanged.Count > 0)
        {
          var idsToName = await snapshot.CameraIdsAsync(valueChanged, ct);
          foreach (var (id, cameraId) in idsToName)
            valueCameras[id] = cameraId;
        }
      }

      var applied = subscription.TryApply(state.Generation, () =>
      {
        subscription.StateVersions = visible.ToDictionary(
          id => id, id => StateVersion(states, id), StringComparer.Ordinal);
        subscription.AlarmVersions = visible.ToDictionary(
          id => id, id => AlarmVersion(alarms, id), StringComparer.Ordinal);
        subscription.SentIds = visible;

        if (state.UpdateValues)
        {
          subscription.ValueVersions = values.ToDictionary(
            kv => kv.Key, kv => kv.Value.Version, StringComparer.Ordinal);
        }
      });
      if (!applied)
        return;

      if (changed.Count > 0)
      {
        Send(subscription, "set_visual_states",
          changed.Select(id => Item(id, cameras.GetValueOrDefault(id))));
      }

      if (lost.Count > 0)
        Send(subscription, "set_ids2delete", lost.Select(id => Item(id, null)));

      if (valueChanged.Count > 0)
      {
        Send(subscription, "on_values_changed",
          valueChanged.Select(id => Item(id, valueCameras.GetValueOrDefault(id))));
      }
    }

    private static uint StateVersion(Dictionary<string, ObjectStateDTO> states, string id) =>
      states.TryGetValue(id, out var state) ? state.Version : 0;

    private static uint AlarmVersion(Dictionary<string, AlarmState> alarms, string id) =>
      alarms.TryGetValue(id, out var alarm) ? alarm.Version : 0;

    private static MapProto.WatchItem Item(string objectId, string? cameraId) => new()
    {
      ObjectId = objectId,
      CameraId = cameraId ?? string.Empty,
    };

    private static void Send(
      WatchSubscription subscription, string action, IEnumerable<MapProto.WatchItem> items)
    {
      var message = new MapProto.WatchMessage { Action = action };
      message.Items.AddRange(items);
      subscription.Send(message);
    }
  }
}
