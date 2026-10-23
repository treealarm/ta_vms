using Analytics;
using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;
using VmsAnalyticsSingleton;
using VmsLog;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var cfgAppId = Require("DAPR_CFG_APP_ID");
var grpcPort = RequirePort("ANALYTICS_VMS_PORT");
var debounceSeconds = RequirePort("ANALYTICS_DEBOUNCE_SECONDS");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();
builder.Services.AddDaprPubSubClient();
builder.AddVmsDebugState();

builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton(_ =>
  new StorageService.StorageServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<ServerPairRegistry>();
builder.Services.AddSingleton<CameraStorageResolver>();
builder.Services.AddSingleton<CameraServerCache>();
// Registered outside the "are there any analytics workers" block below: the ONVIF source reaches
// AnalyticsServiceImpl regardless of whether a CV worker exists.
builder.Services.AddSingleton<MetadataArchiveWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetadataArchiveWriter>());

// video_a workers are optional, mirroring ISquareIntegration/NoOpSquareIntegration in VmsCfg: if
// none are configured, analytics ingestion is simply inactive — no exception, no crash. Multiple
// workers (comma-separated app-ids) are sharded by rendezvous hash in AnalyticsWorkerRegistry.
var workerAppIds = (Environment.GetEnvironmentVariable("DAPR_ANALYTICS_WORKER_APP_IDS") ?? "")
  .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (workerAppIds.Length > 0)
{
  builder.Services.AddSingleton(_ => new AnalyticsWorkerRegistry(
    workerAppIds.ToDictionary(
      appId => appId,
      IAnalyticsWorkerClient (appId) => new AnalyticsWorkerClient(
        new AnalyticsService.AnalyticsServiceClient(DaprClient.CreateInvocationInvoker(appId))))));
  builder.Services.AddSingleton<AnalyticsWatchCache>();
  builder.Services.AddSingleton<WatchReconciler>();
  builder.Services.AddHostedService(sp => sp.GetRequiredService<WatchReconciler>()); // keep-alive loop
  builder.Services.AddHostedService<AnalyticsWatchSubscriber>();
  builder.Services.AddHostedService<DetectionStreamConsumer>();
  builder.Services.AddHostedService<CropJanitor>();
}
// else: AnalyticsWatchCache/WatchReconciler/subscriber/consumer/janitor are not registered —
// there is nothing to reconcile against without a worker to call.

builder.Services.AddSingleton(_ => new DetectionDebouncer(TimeSpan.FromSeconds(debounceSeconds)));
builder.Services.AddSingleton<AnalyticsActivityLog>();
builder.Services.AddSingleton<LiveDetectionBroadcaster>();
builder.Services.AddSingleton<RecentLiveEmbeddingCache>();
// ONVIF events for the monitor. Registered unconditionally, like the broadcaster above: it needs no
// analytics worker, only a camera with an event subscription.
builder.Services.AddSingleton<LiveEventBroadcaster>();
// Re-identification: resolves object_id from embeddings (in-memory gallery) and picks the event_id
// group by time gap. Both are in-memory only (see their class docs); env vars validated on startup.
builder.Services.AddSingleton<IObjectIdentityResolver, EphemeralReidGallery>();
builder.Services.AddSingleton<ObjectSessionService>();

var postgresConnStr = BuildPostgresConnectionString();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnStr));
builder.Services.AddDbContextFactory<AnalyticsDbContext>(options =>
  options.UseNpgsql(postgresConnStr));
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<AnalyticsServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenLocalhost(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();

// ServerPairRegistry needs the media-server<->fs pairing table before the first RetainEvent call.
var registry = app.Services.GetRequiredService<ServerPairRegistry>();
_ = Task.Run(async () =>
{
  while (true)
  {
    try
    {
      await registry.InitAsync();
      break;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"VmsAnalyticsSingleton: ServerPairRegistry init failed, retrying in 5s: {ex.Message}");
      await Task.Delay(5_000);
    }
  }
});

app.MapGrpcService<AnalyticsServiceImpl>();

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
  var dsn = Environment.GetEnvironmentVariable("VMS_ANALYTICS_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");
  if (!string.IsNullOrWhiteSpace(dsn))
    return dsn;

  return $"Host={Require("POSTGRES_HOST")};Port={Require("POSTGRES_PORT")};" +
    $"Database={Require("POSTGRES_ANALYTICS_DB")};Username={Require("POSTGRES_USER")};" +
    $"Password={Require("POSTGRES_PASSWORD")}";
}
