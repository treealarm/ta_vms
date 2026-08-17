using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;
using Vms.Onvif;
using VmsFs;
using VmsLog;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var cfgAppId = Require("DAPR_CFG_APP_ID");
var grpcPort = RequirePort("FS_VMS_PORT");
var ownAppId = Environment.GetEnvironmentVariable("APP_ID");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();
builder.Services.AddDaprPubSubClient();
builder.Services.AddHostedService<CameraConfigSubscriber>();
builder.Services.AddSingleton<ClipCleaner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClipCleaner>());
builder.Services.AddSingleton<AlarmRecordingService>();
var postgresConnStr = BuildPostgresConnectionString();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnStr));
builder.Services.AddDbContextFactory<ClipsDbContext>(options =>
  options.UseNpgsql(postgresConnStr));
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton(_ =>
  new StorageService.StorageServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<CameraConfigCache>();
builder.Services.AddSingleton<CameraSetupCache>();
builder.Services.AddSingleton<StorageListCache>();
builder.Services.AddSingleton<CameraStorageCache>();
builder.Services.AddHostedService<CameraStorageSubscriber>();
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<FsActivityLog>();
builder.Services.AddSingleton<ClipStorage>();
builder.Services.AddSingleton<MetadataSampleStore>();
builder.Services.AddSingleton<FragIndexWarmer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FragIndexWarmer>());
builder.Services.AddSingleton<ExportFileStore>();
builder.Services.AddSingleton<SourceOutageStore>();
builder.Services.AddSingleton<EdgeImportJobStore>();
builder.Services.AddSingleton<SourceLivenessTracker>();
builder.Services.AddSingleton<LivenessSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LivenessSweeper>());
builder.Services.AddSingleton(_ =>
  new OnvifService.OnvifServiceClient(DaprClient.CreateInvocationInvoker(Require("DAPR_ONVIF_APP_ID"))));
builder.Services.AddSingleton<EdgeImportScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EdgeImportScheduler>());
builder.Services.AddHostedService<EdgeExportCleaner>();
builder.Services.AddSingleton<FsServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();

app.MapGrpcService<FsServiceImpl>();

// Resolve own paired server_id from VmsCfg in the background so startup is not blocked.
if (!string.IsNullOrWhiteSpace(ownAppId))
{
  var cfg = app.Services.GetRequiredService<CfgService.CfgServiceClient>();
  var clipCleaner = app.Services.GetRequiredService<ClipCleaner>();
  _ = Task.Run(async () =>
  {
    var ownServerId = await ResolveOwnServerIdAsync(cfg, ownAppId);
    clipCleaner.OwnServerId = ownServerId;
    // Both VmsFs instances share one ta_fs database, so every background service must scope itself
    // to its own node or they would fight over each other's cameras.
    app.Services.GetRequiredService<LivenessSweeper>().SetScope(ownServerId);
    app.Services.GetRequiredService<EdgeImportScheduler>().SetScope(ownServerId);
  });
}
else
{
  // No APP_ID means no pairing to resolve — a single FS instance owning everything.
  app.Services.GetRequiredService<LivenessSweeper>().SetScope(null);
  app.Services.GetRequiredService<EdgeImportScheduler>().SetScope(null);
}

app.Run();

static async Task<string?> ResolveOwnServerIdAsync(CfgService.CfgServiceClient cfg, string fsAppId)
{
  while (true)
  {
    try
    {
      var resp = await cfg.ListMediaServersAsync(new Vms.Cfg.ListMediaServersRequest());
      var match = resp.Servers.FirstOrDefault(s =>
        string.Equals(s.FsId, fsAppId, StringComparison.OrdinalIgnoreCase));
      if (match is not null)
        return match.ServerId;
      Console.Error.WriteLine($"VmsFs: no server paired with fs_id='{fsAppId}' in ListMediaServers — retrying");
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"VmsFs: ResolveOwnServerId failed, retrying in 5s: {ex.Message}");
    }
    await Task.Delay(5_000);
  }
}

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");

static string BuildPostgresConnectionString()
{
  var dsn = Environment.GetEnvironmentVariable("VMS_FS_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");
  if (!string.IsNullOrWhiteSpace(dsn))
    return dsn;

  return $"Host={Require("POSTGRES_HOST")};Port={Require("POSTGRES_PORT")};" +
    $"Database={Require("POSTGRES_FS_DB")};Username={Require("POSTGRES_USER")};" +
    $"Password={Require("POSTGRES_PASSWORD")}";
}
