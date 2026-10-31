using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Watch
{
  // The reads one poll needs, in one scope: the poller is a singleton and the repositories are
  // scoped, so every tick opens a scope and asks this service rather than the repositories.
  internal sealed class WatchSnapshotService
  {
    private readonly IGeoService _geo;
    private readonly IMapService _map;
    private readonly IStateService _states;
    private readonly IValuesService _values;
    private readonly IRightsService _rights;
    private readonly VmsMapDbContext _db;

    public WatchSnapshotService(
      IGeoService geo,
      IMapService map,
      IStateService states,
      IValuesService values,
      IRightsService rights,
      VmsMapDbContext db)
    {
      _geo = geo ?? throw new ArgumentNullException(nameof(geo));
      _map = map ?? throw new ArgumentNullException(nameof(map));
      _states = states ?? throw new ArgumentNullException(nameof(states));
      _values = values ?? throw new ArgumentNullException(nameof(values));
      _rights = rights ?? throw new ArgumentNullException(nameof(rights));
      _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<Dictionary<string, GeoObjectDTO>> GeoAsync(BoxDTO box, CancellationToken ct) =>
      _geo.GetGeoAsync(box);

    public Task<HashSet<string>> VisibleAsync(
      IReadOnlyCollection<string> ids, CallerContext caller, CancellationToken ct) =>
      _rights.VisibleAsync(ids, caller, ct);

    // A replica shows its owner's states and alarms, under its own id — the same answer the REST
    // reads give, so a watcher sees a replica change when its owner does.
    public Task<Dictionary<string, ObjectStateDTO>> StatesAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct) =>
      OwnerView.StatesAsync(_map, _states, ids);

    public Task<Dictionary<string, AlarmState>> AlarmsAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct) =>
      OwnerView.AlarmsAsync(_map, _states, ids);

    public Task<Dictionary<string, string>> CameraIdsAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct) =>
      CameraIdLookup.ResolveAsync(_db, ids, ct);

    // A replica shows its owner's values; the message names the node the subscriber asked about.
    public async Task<Dictionary<string, ValueDTO>> ValuesAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct)
    {
      var owners = await ValueOwnersAsync(ids, ct);
      return await _values.GetListByOwnersAsync(
        owners.Values.Distinct(StringComparer.Ordinal).ToList());
    }

    public async Task<Dictionary<string, string>> ValueOwnersAsync(
      IReadOnlyCollection<string> ids, CancellationToken ct)
    {
      var ownersAndViews = await _map.GetOwnersAndViewsAsync(ids.ToList());
      return ids.ToDictionary(
        id => id,
        id => string.IsNullOrEmpty(ownersAndViews.GetValueOrDefault(id)?.owner_id)
          ? id
          : ownersAndViews[id].owner_id!,
        StringComparer.Ordinal);
    }
  }
}
