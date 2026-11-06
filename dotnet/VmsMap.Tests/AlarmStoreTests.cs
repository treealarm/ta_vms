using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Adapter;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The alarm entity's transition table, on the in-memory provider: what each operation does from
// each status, what it writes, and when ALARM comes off the node.
public class AlarmStoreTests
{
  private sealed record Services(
    VmsMapDbContext Db, MapDb Map, AlarmStore Store, TestCameraState Arming);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var statesUpdate = new StatesUpdateService(stateService, map, db);
    var arming = new TestCameraState();

    return new Services(db, map, TestWiring.Alarms(db, map, statesUpdate, arming), arming);
  }

  /// <summary>A camera node with one digital input under it, registered in device_node.</summary>
  private static async Task<(Guid Camera, Guid Input)> DeviceAsync(
    Services s, string cameraId = TestCameras.One)
  {
    var camera = Guid.Parse(CameraObjectId.For(cameraId));
    var input = Guid.Parse(DeviceObjectId.For(
      DeviceObjectId.CameraSource, cameraId, DeviceObjectId.DigitalInputPart, "1"));

    await s.Map.UpdateHierarchyAsync([
      new BaseMarkerDTO { id = camera.ToString(), name = "Camera" },
      new BaseMarkerDTO { id = input.ToString(), parent_id = camera.ToString(), name = "Door" },
    ]);

    s.Db.DeviceNodes.Add(TestCameras.DeviceNode(cameraId, camera));
    s.Db.DeviceNodes.Add(new DBDeviceNode
    {
      object_id = input,
      source_kind = DeviceObjectId.CameraSource,
      source_id = cameraId,
      part_kind = DeviceObjectId.DigitalInputPart,
      part_token = "1",
    });
    await s.Db.SaveChangesAsync();

    return (camera, input);
  }

  private static RaiseAlarmSpec Sensor(Guid objectId, string key = "sensor:1", string? trigger = "open") =>
    new(objectId, AlarmSource.Sensor, key, Trigger: trigger);

  private static Task<DBAlarm?> ById(VmsMapDbContext db, Guid id) =>
    db.Alarms.AsNoTracking().FirstOrDefaultAsync(a => a.id == id);

  private static async Task<HashSet<string>> States(VmsMapDbContext db, Guid objectId)
  {
    var row = await db.ObjectStates.AsNoTracking().Include(s => s.states)
      .FirstOrDefaultAsync(s => s.id == objectId);
    return row is null
      ? []
      : row.states.Select(v => v.state).ToHashSet(StringComparer.Ordinal);
  }

  private static Task<List<string>> Events(VmsMapDbContext db) =>
    db.Events.AsNoTracking().Select(e => e.event_name).ToListAsync();

  [Fact]
  public async Task RaiseIsIdempotentByDedupKey()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var first = await s.Store.RaiseAsync(Sensor(input), default);
    var second = await s.Store.RaiseAsync(Sensor(input), default);

    Assert.Equal(AlarmMove.Applied, first.Move);
    Assert.Equal(AlarmMove.Unchanged, second.Move);
    Assert.Single(await s.Db.Alarms.ToListAsync());
    Assert.Equal(1, (await Events(s.Db)).Count(e => e == AlarmStore.RaisedEvent));
    Assert.Contains(CameraStateWriter.Alarm, await States(s.Db, input));
  }

  [Fact]
  public async Task ARaiseAfterReturnedComesBackActiveWithOneNewEvent()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var raised = await s.Store.RaiseAsync(Sensor(input), default);
    var id = raised.Alarm!.id;

    Assert.Equal(AlarmMove.Applied, (await s.Store.NormalAsync(input, "sensor:1", default)).Move);
    Assert.Equal(AlarmStatus.Returned, (await ById(s.Db, id))!.status);

    var again = await s.Store.RaiseAsync(Sensor(input), default);

    Assert.Equal(AlarmMove.Applied, again.Move);
    Assert.Equal(id, again.Alarm!.id);
    Assert.Equal(AlarmStatus.Active, (await ById(s.Db, id))!.status);
    Assert.True((await ById(s.Db, id))!.condition_active);
    Assert.Null((await ById(s.Db, id))!.cleared_at);
    Assert.Equal(2, (await Events(s.Db)).Count(e => e == AlarmStore.RaisedEvent));
  }

  [Fact]
  public async Task AcknowledgingALiveConditionIsRefusedWhileArmed()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);
    s.Arming.Armed = true;

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;

    var outcome = await s.Store.AcknowledgeAsync(alarm.id, "alice", "seen", default);

    Assert.Equal(AlarmMove.Refused, outcome.Move);
    Assert.Equal(AlarmStatus.Active, (await ById(s.Db, alarm.id))!.status);
  }

  [Fact]
  public async Task ADisarmedNodeMayBeAcknowledgedWithALiveCondition()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);
    s.Arming.Armed = false;

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;
    var outcome = await s.Store.AcknowledgeAsync(alarm.id, "alice", "checking", default);

    Assert.Equal(AlarmMove.Applied, outcome.Move);

    var row = await ById(s.Db, alarm.id);
    Assert.Equal(AlarmStatus.Acknowledged, row!.status);
    Assert.True(row.condition_active);
    Assert.Equal("alice", row.acknowledged_by);
    Assert.Equal("checking", row.ack_comment);
    Assert.Contains(CameraStateWriter.Alarm, await States(s.Db, input));

    var evt = await s.Db.Events.Include(e => e.extra_props)
      .SingleAsync(e => e.event_name == AlarmStore.AcknowledgedEvent);
    var props = evt.extra_props.ToDictionary(p => p.prop_name!, p => p.str_val);
    Assert.Equal(alarm.id.ToString(), props["alarm_id"]);
    Assert.Equal("alice", props["acknowledged_by"]);
    Assert.Equal("checking", props["comment"]);
    Assert.Equal(TestCameras.One, props["camera_id"]);
  }

  [Fact]
  public async Task AcknowledgingAnAlarmWhoseConditionClearedClosesIt()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;
    await s.Store.NormalAsync(input, "sensor:1", default);

    var outcome = await s.Store.AcknowledgeAsync(alarm.id, "alice", null, default);

    Assert.Equal(AlarmMove.Applied, outcome.Move);

    var row = await ById(s.Db, alarm.id);
    Assert.Equal(AlarmStatus.Closed, row!.status);
    Assert.Equal(AlarmCloseReason.AcknowledgedAfterNormal, row.close_reason);
    Assert.Equal("alice", row.closed_by);

    var events = await Events(s.Db);
    Assert.Contains(AlarmStore.AcknowledgedEvent, events);
    Assert.Contains(AlarmStore.ClosedEvent, events);
  }

  [Fact]
  public async Task NormalFromAcknowledgedClosesWithTheAcknowledgerCredited()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;
    await s.Store.AcknowledgeAsync(alarm.id, "alice", null, default);
    await s.Store.NormalAsync(input, "sensor:1", default);

    var row = await ById(s.Db, alarm.id);
    Assert.Equal(AlarmStatus.Closed, row!.status);
    Assert.Equal(AlarmCloseReason.ReturnedToNormal, row.close_reason);
    Assert.Equal("alice", row.closed_by);
    Assert.False(row.condition_active);
    Assert.NotNull(row.cleared_at);
    Assert.NotNull(row.closed_at);
  }

  [Fact]
  public async Task AnAutoOnNormalDefinitionClosesWhenTheConditionLeaves()
  {
    var s = Create();
    var (camera, _) = await DeviceAsync(s);

    // A retention alarm uses the seeded auto_on_normal definition and stands on the camera node.
    var outcome = await s.Store.RaiseAsync(
      new RaiseAlarmSpec(camera, AlarmSource.Retention, "retention:1",
        ExpectedEndTs: DateTime.UtcNow.AddMinutes(5)), default);

    var normal = await s.Store.NormalAsync(camera, "retention:1", default);

    Assert.Equal(AlarmMove.Applied, normal.Move);

    var row = await ById(s.Db, outcome.Alarm!.id);
    Assert.Equal(AlarmStatus.Closed, row!.status);
    Assert.Equal(AlarmCloseReason.AutoReturnedToNormal, row.close_reason);
    Assert.Equal("system", row.closed_by);
    Assert.Contains(AlarmStore.ClosedEvent, await Events(s.Db));

    // Retention writes no raised event of its own.
    Assert.DoesNotContain(AlarmStore.RaisedEvent, await Events(s.Db));
  }

  [Fact]
  public async Task ClosingByHandIsRefusedWhileTheConditionIsActive()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;
    var outcome = await s.Store.CloseAsync(alarm.id, "alice", AlarmCloseReason.FalseAlarm, null, default);

    Assert.Equal(AlarmMove.Refused, outcome.Move);
    Assert.Equal(AlarmStatus.Active, (await ById(s.Db, alarm.id))!.status);
  }

  [Fact]
  public async Task ADroppedPartClosesEveryOpenAlarmOnIt()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var first = (await s.Store.RaiseAsync(Sensor(input, "sensor:1"), default)).Alarm!;
    var second = (await s.Store.RaiseAsync(Sensor(input, "sensor:2"), default)).Alarm!;

    await s.Store.CloseForDeviceGoneAsync(input, default);

    foreach (var id in new[] { first.id, second.id })
    {
      var row = await ById(s.Db, id);
      Assert.Equal(AlarmStatus.Closed, row!.status);
      Assert.Equal(AlarmCloseReason.DeviceGone, row.close_reason);
      Assert.Equal("system", row.closed_by);
      Assert.False(row.condition_active);
      Assert.NotNull(row.cleared_at);
    }

    Assert.DoesNotContain(CameraStateWriter.Alarm, await States(s.Db, input));
  }

  [Fact]
  public async Task AlarmStaysUntilNothingOnTheNodeIsStillTrue()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    await s.Store.RaiseAsync(Sensor(input, "sensor:1"), default);
    await s.Store.RaiseAsync(Sensor(input, "sensor:2"), default);

    await s.Store.NormalAsync(input, "sensor:1", default);
    Assert.Contains(CameraStateWriter.Alarm, await States(s.Db, input));

    await s.Store.NormalAsync(input, "sensor:2", default);
    Assert.DoesNotContain(CameraStateWriter.Alarm, await States(s.Db, input));
  }

  [Fact]
  public async Task ACameraGoesBackToNormWhenItsAlarmClears()
  {
    var s = Create();
    var (camera, _) = await DeviceAsync(s);

    await s.Store.RaiseAsync(new RaiseAlarmSpec(camera, AlarmSource.Sensor, "sensor:camera"), default);
    Assert.Contains(CameraStateWriter.Alarm, await States(s.Db, camera));

    await s.Store.NormalAsync(camera, "sensor:camera", default);

    var states = await States(s.Db, camera);
    Assert.DoesNotContain(CameraStateWriter.Alarm, states);
    Assert.Contains(CameraStateWriter.Norm, states);
  }

  [Fact]
  public async Task ATrailingCloseOfAClosedAlarmChangesNothing()
  {
    var s = Create();
    var (_, input) = await DeviceAsync(s);

    var alarm = (await s.Store.RaiseAsync(Sensor(input), default)).Alarm!;
    await s.Store.NormalAsync(input, "sensor:1", default);
    await s.Store.AcknowledgeAsync(alarm.id, "alice", null, default);

    var again = await s.Store.AcknowledgeAsync(alarm.id, "alice", null, default);
    Assert.Equal(AlarmMove.Refused, again.Move);

    var close = await s.Store.CloseAsync(alarm.id, "alice", AlarmCloseReason.Other, null, default);
    Assert.Equal(AlarmMove.Unchanged, close.Move);
  }
}
