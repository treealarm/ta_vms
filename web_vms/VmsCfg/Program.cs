using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SquareIntegrationClient;
using VmsCfg;
using VmsLog;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = RequirePort("CFG_VMS_PORT");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();
var postgresConnStr = BuildPostgresConnectionString();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnStr));
builder.Services.AddDbContextFactory<VmsCfgDbContext>(options =>
  options.UseNpgsql(postgresConnStr));
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<ScheduleDb>();
builder.Services.AddSingleton<ScheduleCache>();
builder.Services.AddSingleton<CameraStorage>();
builder.Services.AddSingleton<StorageDb>();
builder.Services.AddSingleton<PermissionDb>();
builder.Services.AddSingleton<CfgServiceImpl>();
builder.Services.AddSingleton<StorageServiceImpl>();
builder.Services.AddHostedService<RecordingScheduleService>();

// SQUARE_INTEGRATION_SINK_APP_ID is intentionally optional: when unset, VmsCfg runs fully
// standalone with no dependency on Square (no-op ISquareIntegration, no subscriber registered).
// When set, it must name Square's receiving Dapr app-id (its integrationhost service, not the
// user-facing leafletalarms app) — see docs/square-integration-plan.md.
var squareSinkAppId = Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_SINK_APP_ID");
var squareEnabled = !string.IsNullOrEmpty(squareSinkAppId);
if (squareEnabled)
{
  builder.Services.AddSingleton<ISquareIntegration>(
    _ => new SquareIntegrationGrpcClient(squareSinkAppId!, Require("APP_ID")));
  builder.Services.AddDaprPubSubClient();
  builder.Services.AddHostedService<SquareAlarmSubscriber>();
  builder.Services.AddHostedService<SquareResyncService>();
}
else
{
  builder.Services.AddSingleton<ISquareIntegration, NoOpSquareIntegration>();
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();
// Loaded before the first request: ListCameras resolves schedule_active from this cache, and an
// empty cache would report "not recording" for a camera whose window is currently open.
await app.Services.GetRequiredService<ScheduleCache>().ReloadAsync(CancellationToken.None);
app.MapGrpcService<CfgServiceImpl>();
app.MapGrpcService<StorageServiceImpl>();

app.Run();

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");

static string BuildPostgresConnectionString()
{
  var dsn = Environment.GetEnvironmentVariable("VMS_CFG_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");
  if (!string.IsNullOrWhiteSpace(dsn))
    return dsn;

  return $"Host={Require("POSTGRES_HOST")};Port={Require("POSTGRES_PORT")};" +
    $"Database={Require("POSTGRES_CFG_DB")};Username={Require("POSTGRES_USER")};" +
    $"Password={Require("POSTGRES_PASSWORD")}";
}
