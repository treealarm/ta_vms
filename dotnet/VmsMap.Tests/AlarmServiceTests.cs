using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The retention adapter on the in-memory provider: the episode becomes an alarm row and one legacy
// journal event, a renewal moves the projected end, the sweep closes only after VmsFs says the
// episode is over, and connectivity never displaces the alarm.
public class AlarmServiceTests
{
  private sealed class FakeRetention : IRetentionEventSource
  {
    public RetentionEpisode? Next { get; set; }

    public Task<RetentionEpisode?> FindAsync(
      string cameraId, DateTime alarmStartTs, CancellationToken ct) =>
      Task.FromResult(Next);
  }

  private sealed record Services(
    VmsMapDbContext Db,
    MapDb Map,
    CameraStateWriter States,
    AlarmStore Alarms,
    AlarmService Retention,
    FakeRetention Source);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var writer = new CameraStateWriter(new ObjectStateWriter(db, stateService, statesUpdate));
    var source = new FakeRetention();
    var alarms = TestWiring.Alarms(db, map, statesUpdate);
    var retention = new AlarmService(
      db, alarms, new EventsWriter(db, new EventsService(db, map)), source,
      NullLogger<AlarmService>.Instance);

    return new Services(db, map, writer, alarms, retention, source);
  }

  private static async Task<BaseMarkerDTO> Camera(
    VmsMapDbContext db, MapDb map, string cameraId = TestCameras.One)
  {
    var node = new BaseMarkerDTO
    {
      id = CameraObjectId.For(cameraId),
      name = "Camera " + cameraId,
    };
    await map.UpdateHierarchyAsync([node]);
    db.DeviceNodes.Add(TestCameras.DeviceNode(cameraId, Guid.Parse(node.id!)));
    await db.SaveChangesAsync();
    return node;
  }

  private static RetentionEpisode Episode(
    string? id = null, DateTime? end = null, string? stream = "main", string? trigger = "motion",
    bool raiseAlarm = true) =>
    new(DateTime.UtcNow, end, stream, trigger, id ?? Guid.NewGuid().ToString(), raiseAlarm);

  private static async Task<string?> State(VmsMapDbContext db, string cameraId)
  {
    var id = Guid.Parse(CameraObjectId.For(cameraId));
    var row = await db.ObjectStates
      .Include(s => s.states)
      .FirstOrDefaultAsync(s => s.id == id);
    if (row is null)
      return null;

    return string.Join(",", row.states.Select(s => s.state).OrderBy(s => s));
  }

  private static Task<DBAlarm?> Open(VmsMapDbContext db) =>
    db.Alarms.AsNoTracking().OrderByDescending(a => a.raised_at).FirstOrDefaultAsync();

  [Fact]
  public async Task AnActiveEpisodeWritesAlarmAndOneEvent()
  {
    var s = Create();
    var camera = await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));

    await s.Retention.RecordAsync(TestCameras.One, episode, default);

    Assert.Equal(CameraStateWriter.Alarm, await State(s.Db, TestCameras.One));

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm);
    Assert.Equal(AlarmSource.Retention, alarm!.source);
    Assert.Equal($"retention:{episode.EventUuid}", alarm.dedup_key);
    Assert.Equal(AlarmStatus.Active, alarm.status);
    Assert.True(alarm.condition_active);
    Assert.NotNull(alarm.expected_end_ts);

    // Retention keeps its legacy `alarm` event; no second alarm_raised is written for it.
    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    Assert.Equal(Guid.Parse(episode.EventUuid), evt.id);
    Assert.Equal(Guid.Parse(camera.id!), evt.object_id);
    Assert.Equal("alarm", evt.event_name);
    Assert.Equal("main", evt.param0);
    Assert.Equal("motion", evt.param1);
    Assert.Contains(evt.extra_props, p => p.prop_name == "alarm_end_ts" && !string.IsNullOrEmpty(p.str_val));
  }

  [Fact]
  public async Task AnEpisodeAlreadyOverRaisesNothingAndWritesItsEvent()
  {
    var s = Create();
    await Camera(s.Db, s.Map);

    await s.Retention.RecordAsync(TestCameras.One, Episode(end: DateTime.UtcNow.AddMinutes(-1)), default);

    Assert.Null(await Open(s.Db));
    Assert.Null(await State(s.Db, TestCameras.One));
    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task FootageIsNotAnAlarm()
  {
    var s = Create();
    await Camera(s.Db, s.Map);

    await s.Retention.RecordAsync(
      TestCameras.One, Episode(end: DateTime.UtcNow.AddMinutes(5), raiseAlarm: false), default);

    Assert.Null(await Open(s.Db));
    Assert.Null(await State(s.Db, TestCameras.One));
    Assert.Empty(await s.Db.Events.ToListAsync());
  }

  [Fact]
  public async Task RedeliveryDoesNotWriteTheEventTwice()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));

    await s.Retention.RecordAsync(TestCameras.One, episode, default);
    await s.Retention.RecordAsync(TestCameras.One, episode, default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
    Assert.Equal(1, await s.Db.Alarms.CountAsync());
  }

  [Fact]
  public async Task AReNewalMovesTheRecordedEnd()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var id = Guid.NewGuid().ToString();
    var first = Episode(id, DateTime.UtcNow.AddMinutes(1));
    var second = Episode(id, DateTime.UtcNow.AddMinutes(10));

    await s.Retention.RecordAsync(TestCameras.One, first, default);
    await s.Retention.RecordAsync(TestCameras.One, second, default);

    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    var end = evt.extra_props.Single(p => p.prop_name == "alarm_end_ts").str_val;
    Assert.Contains(second.AlarmEndTs!.Value.ToString("O")[..16], end);

    var alarm = await Open(s.Db);
    Assert.NotNull(alarm!.expected_end_ts);
    Assert.Equal(second.AlarmEndTs.Value.ToString("O")[..16], alarm.expected_end_ts.Value.ToString("O")[..16]);
  }

  [Fact]
  public async Task TheSweepClosesAnAlarmWhenVmsFsConfirmsTheEnd()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));
    await s.Retention.RecordAsync(TestCameras.One, episode, default);

    // VmsFs now says the episode is over, and the projection on the row is already past.
    await s.Db.Alarms.ForEachAsync(a => a.expected_end_ts = DateTime.UtcNow.AddMinutes(-1));
    await s.Db.SaveChangesAsync();
    s.Source.Next = Episode(episode.EventUuid, DateTime.UtcNow.AddMinutes(-1));

    await s.Retention.SweepAsync(default);

    var alarm = await Open(s.Db);
    Assert.Equal(AlarmStatus.Closed, alarm!.status);
    Assert.Equal(AlarmCloseReason.AutoReturnedToNormal, alarm.close_reason);
    Assert.Equal("system", alarm.closed_by);
    Assert.Equal(CameraStateWriter.Norm, await State(s.Db, TestCameras.One));
    Assert.Contains(
      await s.Db.Events.Select(e => e.event_name).ToListAsync(),
      name => name == AlarmStore.ClosedEvent);
  }

  [Fact]
  public async Task TheSweepKeepsAnAlarmThatWasRenewed()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));
    await s.Retention.RecordAsync(TestCameras.One, episode, default);

    var renewed = DateTime.UtcNow.AddMinutes(10);
    await s.Db.Alarms.ForEachAsync(a => a.expected_end_ts = DateTime.UtcNow.AddMinutes(-1));
    await s.Db.SaveChangesAsync();
    s.Source.Next = Episode(episode.EventUuid, renewed);

    await s.Retention.SweepAsync(default);

    var alarm = await Open(s.Db);
    Assert.Equal(AlarmStatus.Active, alarm!.status);
    Assert.Equal(renewed.ToString("O")[..16], alarm.expected_end_ts!.Value.ToString("O")[..16]);
    Assert.Equal(CameraStateWriter.Alarm, await State(s.Db, TestCameras.One));
  }

  [Fact]
  public async Task ConnectivityDoesNotDisplaceTheAlarm()
  {
    var s = Create();
    await Camera(s.Db, s.Map);

    await s.States.SetAlarmAsync(TestCameras.One, default);
    await s.States.SetConnectedAsync(TestCameras.One, true, default);

    var states = await State(s.Db, TestCameras.One);
    Assert.Contains(CameraStateWriter.Alarm, states);
    Assert.Contains(CameraStateWriter.Connected, states);

    await s.States.SetConnectedAsync(TestCameras.One, false, default);

    states = await State(s.Db, TestCameras.One);
    Assert.Contains(CameraStateWriter.Alarm, states);
    Assert.Contains(CameraStateWriter.Disconnected, states);
    Assert.DoesNotContain(
      CameraStateWriter.Connected, states!.Split(',', StringSplitOptions.RemoveEmptyEntries));
  }
}
