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

// The retention adapter on the in-memory provider: the event is the episode id, a renewal moves
// the recorded end, the sweep takes ALARM away only after VmsFs says the episode is over, and
// connectivity never displaces the alarm.
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
    AlarmService Alarms,
    FakeRetention Retention);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var writer = new CameraStateWriter(db, stateService, statesUpdate);
    var retention = new FakeRetention();
    var alarms = new AlarmService(
      db, writer, new EventsService(db, map), retention,
      NullLogger<AlarmService>.Instance);

    return new Services(db, map, writer, alarms, retention);
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
    db.CameraNodes.Add(new DBCameraNode
    {
      object_id = Guid.Parse(node.id!),
      camera_id = cameraId,
    });
    await db.SaveChangesAsync();
    return node;
  }

  private static RetentionEpisode Episode(
    string? id = null, DateTime? end = null, string? stream = "main", string? trigger = "motion") =>
    new(DateTime.UtcNow, end, stream, trigger, id ?? Guid.NewGuid().ToString());

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

  [Fact]
  public async Task AnActiveEpisodeWritesAlarmAndOneEvent()
  {
    var s = Create();
    var camera = await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));

    await s.Alarms.RecordAsync(TestCameras.One, episode, default);

    Assert.Equal(CameraStateWriter.Alarm, await State(s.Db, TestCameras.One));

    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    Assert.Equal(Guid.Parse(episode.EventUuid), evt.id);
    Assert.Equal(Guid.Parse(camera.id!), evt.object_id);
    Assert.Equal("alarm", evt.event_name);
    Assert.Equal("main", evt.param0);
    Assert.Equal("motion", evt.param1);
    Assert.Contains(evt.extra_props, p => p.prop_name == "alarm_end_ts" && !string.IsNullOrEmpty(p.str_val));
  }

  [Fact]
  public async Task AnEpisodeAlreadyOverWritesNormAndItsEvent()
  {
    var s = Create();
    await Camera(s.Db, s.Map);

    await s.Alarms.RecordAsync(TestCameras.One, Episode(end: DateTime.UtcNow.AddMinutes(-1)), default);

    Assert.Equal(CameraStateWriter.Norm, await State(s.Db, TestCameras.One));
    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task RedeliveryDoesNotWriteTheEventTwice()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));

    await s.Alarms.RecordAsync(TestCameras.One, episode, default);
    await s.Alarms.RecordAsync(TestCameras.One, episode, default);

    Assert.Equal(1, await s.Db.Events.CountAsync());
  }

  [Fact]
  public async Task AReNewalMovesTheRecordedEnd()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var id = Guid.NewGuid().ToString();
    var first = Episode(id, DateTime.UtcNow.AddMinutes(1));
    var second = Episode(id, DateTime.UtcNow.AddMinutes(10));

    await s.Alarms.RecordAsync(TestCameras.One, first, default);
    await s.Alarms.RecordAsync(TestCameras.One, second, default);

    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    var end = evt.extra_props.Single(p => p.prop_name == "alarm_end_ts").str_val;
    Assert.Contains(second.AlarmEndTs!.Value.ToString("O")[..16], end);
  }

  [Fact]
  public async Task TheSweepClearsAnAlarmWhenVmsFsConfirmsTheEnd()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));
    await s.Alarms.RecordAsync(TestCameras.One, episode, default);

    // Move the recorded end into the past without touching the state, as a stale projection would.
    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    evt.extra_props.Single(p => p.prop_name == "alarm_end_ts").str_val =
      DateTime.UtcNow.AddMinutes(-1).ToString("O");
    await s.Db.SaveChangesAsync();

    s.Retention.Next = Episode(episode.EventUuid, DateTime.UtcNow.AddMinutes(-1));
    await s.Alarms.SweepAsync(default);

    Assert.Equal(CameraStateWriter.Norm, await State(s.Db, TestCameras.One));
  }

  [Fact]
  public async Task TheSweepKeepsAnAlarmThatWasRenewed()
  {
    var s = Create();
    await Camera(s.Db, s.Map);
    var episode = Episode(end: DateTime.UtcNow.AddMinutes(5));
    await s.Alarms.RecordAsync(TestCameras.One, episode, default);

    var evt = await s.Db.Events.Include(e => e.extra_props).SingleAsync();
    evt.extra_props.Single(p => p.prop_name == "alarm_end_ts").str_val =
      DateTime.UtcNow.AddMinutes(-1).ToString("O");
    await s.Db.SaveChangesAsync();

    s.Retention.Next = Episode(episode.EventUuid, DateTime.UtcNow.AddMinutes(10));
    await s.Alarms.SweepAsync(default);

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
