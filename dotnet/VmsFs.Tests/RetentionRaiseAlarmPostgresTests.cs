using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Vms.Cfg;
using Vms.Fs;
using VmsDb;
using VmsFs;

namespace VmsFs.Tests;

// Whether a retention episode is an alarm at all is a column, and only PostgreSQL can prove that a
// renewal does not rewrite it. Enabled by VMS_FS_TEST_DSN pointing at a scratch database, as
// VmsCfg's and VmsMap's Postgres tests are.
public sealed class RetentionRaiseAlarmPostgresTests
{
  private static readonly string? Dsn = Environment.GetEnvironmentVariable("VMS_FS_TEST_DSN");

  private sealed class UnknownCamera : CfgService.CfgServiceClient
  {
    public UnknownCamera() : base(DaprClient.CreateInvocationInvoker("no-such-cfg")) { }

    public override AsyncUnaryCall<GetCameraResponse> GetCameraAsync(
      SourceRequest request, CallOptions options)
    {
      // No camera in VmsCfg: RetainEvent uses the default pre/post seconds and carries on.
      var answer = new GetCameraResponse { Found = false };
      return new AsyncUnaryCall<GetCameraResponse>(
        Task.FromResult(answer), Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess, () => [], () => { });
    }
  }

  [Fact]
  public async Task ARenewalKeepsTheEpisodesRaiseAlarmFlag()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(migrationSource, "VmsFsTest", AppContext.BaseDirectory, "7743512879")
        .MigrateAsync();
    }

    await using var source = new NpgsqlDataSourceBuilder(Dsn).Build();
    var options = new DbContextOptionsBuilder<ClipsDbContext>().UseNpgsql(source).Options;
    var storage = new ClipStorage(
      new PooledDbContextFactory<ClipsDbContext>(options),
      source,
      new CameraConfigCache(new UnknownCamera()),
      null!,
      null!);

    var cameraId = Guid.NewGuid().ToString();

    // An episode that is footage, not an alarm.
    var first = await storage.RetainEventAsync(new RetainEventRequest
    {
      EventId = "motion",
      CameraId = cameraId,
      AlarmStartTs = Timestamp.FromDateTime(DateTime.UtcNow),
      TimeoutSeconds = 30,
      RaiseAlarm = false,
    }, default);

    Assert.False(first.RaiseAlarm);

    // A renewal that does not mention the field must not turn it into an alarm.
    var renewal = await storage.RetainEventAsync(new RetainEventRequest
    {
      EventId = "motion",
      CameraId = cameraId,
      AlarmStartTs = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(5)),
      TimeoutSeconds = 30,
    }, default);

    Assert.Equal(first.EventUuid, renewal.EventUuid);
    Assert.False(renewal.RaiseAlarm);

    var listed = await storage.ListRetentionEventsAsync(50, null, cameraId, null, null, default);
    var row = Assert.Single(listed.Events);
    Assert.True(row.HasRaiseAlarm);
    Assert.False(row.RaiseAlarm);

    // A fresh episode defaults to true: a caller that never heard of the field keeps today's
    // behaviour.
    var alarm = await storage.RetainEventAsync(new RetainEventRequest
    {
      EventId = "tamper",
      CameraId = cameraId,
      AlarmStartTs = Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(1)),
      TimeoutSeconds = 30,
    }, default);

    Assert.True(alarm.RaiseAlarm);
  }
}
