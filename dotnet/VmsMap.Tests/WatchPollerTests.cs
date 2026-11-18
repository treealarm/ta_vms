using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Watch;
using MapProto = Vms.Map;

namespace VmsMap.Tests;

// The box watch reads the same capped rectangle as the canvas read, so it has to say when the
// rectangle held more than one read returned. The statement is a state message of its own: sent on
// the first poll of a scope and whenever the answer changes, never repeated by a quiet tick, and
// never sent by an id scope, which lists its nodes explicitly.
public class WatchPollerTests
{
  private sealed record Env(VmsMapDbContext Db, GeoService Geo, WatchSnapshotService Snapshot);

  private static readonly CallerContext Admin = new(null, ["admin"], true);

  private static Env Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var geo = new GeoService(db, new LevelService(db));
    var snapshot = new WatchSnapshotService(
      geo, new MapDb(db), new StateService(db), new ValuesService(db), new RightsService(db), db);
    return new Env(db, geo, snapshot);
  }

  private static FigureGeoDTO Point(string id, double lat, double lon) => new()
  {
    id = id,
    geometry = new GeometryCircleDTO(new Geo2DCoordDTO { Lat = lat, Lon = lon }),
  };

  /// <summary>A rectangle that holds every point these tests seed, capped as the test needs.</summary>
  private static BoxDTO Box(int count) => new() { wn = [0, 0], es = [1, 1], count = count };

  private static string[] Ids(int count) =>
    Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray();

  private static async Task<List<MapProto.WatchMessage>> Poll(
    WatchSubscription subscription, WatchSnapshotService snapshot)
  {
    await MapWatchPoller.PollAsync(subscription, snapshot, CancellationToken.None);

    var messages = new List<MapProto.WatchMessage>();
    while (subscription.Messages.Reader.TryRead(out var message))
      messages.Add(message);
    return messages;
  }

  [Fact]
  public async Task ATruncatedBoxIsAnnouncedBeforeTheDiffItExplains()
  {
    var env = Create();
    var ids = Ids(3);
    await env.Geo.CreateGeo(ids.Select((id, i) => Point(id, 0.5, 0.5 + i * 0.01)));

    using var subscription = new WatchSubscription();
    subscription.SetBox(Box(2), Admin);

    var messages = await Poll(subscription, env.Snapshot);

    var announcement = messages[0];
    Assert.Equal("set_box_truncated", announcement.Action);
    Assert.True(announcement.Truncated);
    Assert.Empty(announcement.Items);

    var update = Assert.Single(messages, m => m.Action == "set_ids2update");
    // The cut is a prefix of the read's order, which is the database's order by id.
    var prefix = ids
      .Select(Guid.Parse)
      .OrderBy(id => id)
      .Take(2)
      .Select(id => id.ToString())
      .ToList();
    Assert.Equal(prefix, update.Items.Select(item => item.ObjectId));
  }

  [Fact]
  public async Task AQuietTickRepeatsNothing()
  {
    var env = Create();
    await env.Geo.CreateGeo(Ids(3).Select((id, i) => Point(id, 0.5, 0.5 + i * 0.01)));

    using var subscription = new WatchSubscription();
    subscription.SetBox(Box(2), Admin);
    await Poll(subscription, env.Snapshot);

    var messages = await Poll(subscription, env.Snapshot);

    Assert.Empty(messages);
  }

  [Fact]
  public async Task ABoxThatFitsIsAnnouncedAsWhole()
  {
    var env = Create();
    await env.Geo.CreateGeo(Ids(2).Select((id, i) => Point(id, 0.5, 0.5 + i * 0.01)));

    using var subscription = new WatchSubscription();
    subscription.SetBox(Box(2), Admin);

    var messages = await Poll(subscription, env.Snapshot);

    var announcement = Assert.Single(messages, m => m.Action == "set_box_truncated");
    Assert.False(announcement.Truncated);
  }

  [Fact]
  public async Task ABoxThatShrinksIsAnnouncedAsWholeAgain()
  {
    var env = Create();
    var ids = Ids(3);
    await env.Geo.CreateGeo(ids.Select((id, i) => Point(id, 0.5, 0.5 + i * 0.01)));

    using var subscription = new WatchSubscription();
    subscription.SetBox(Box(2), Admin);
    await Poll(subscription, env.Snapshot);

    // The figure beyond the cut goes: nothing the watcher was sent leaves the rectangle, so the
    // completeness of the box is the only change there is to state.
    var unsent = ids.Select(Guid.Parse).OrderBy(id => id).Last().ToString();
    await env.Geo.RemoveAsync([unsent]);

    var messages = await Poll(subscription, env.Snapshot);

    var announcement = Assert.Single(messages);
    Assert.Equal("set_box_truncated", announcement.Action);
    Assert.False(announcement.Truncated);
  }

  [Fact]
  public async Task AnIdScopeNeverAnnouncesTruncation()
  {
    var env = Create();
    var ids = Ids(3);
    await env.Geo.CreateGeo(ids.Select((id, i) => Point(id, 0.5, 0.5 + i * 0.01)));

    using var subscription = new WatchSubscription();
    subscription.SetIds(ids.Take(1).ToList(), Admin);

    var messages = await Poll(subscription, env.Snapshot);

    Assert.DoesNotContain(messages, m => m.Action == "set_box_truncated");
  }
}
