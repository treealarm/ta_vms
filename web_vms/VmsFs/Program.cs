using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;
using VmsFs;

var builder = WebApplication.CreateBuilder(args);

var cfgAppId = Require("DAPR_CFG_APP_ID");
var grpcPort = RequirePort("FS_VMS_PORT");
var ownAppId = Environment.GetEnvironmentVariable("APP_ID");

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();
builder.Services.AddHostedService<CameraConfigSubscriber>();
builder.Services.AddSingleton<ClipCleaner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ClipCleaner>());
builder.Services.AddSingleton<AlarmRecordingService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmRecordingService>());
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
builder.Services.AddSingleton<ClipStorage>();
builder.Services.AddSingleton<FsServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();

// Resolve own paired server_id from VmsCfg (used by ClipCleaner to scope cleanup to own cameras).
if (!string.IsNullOrWhiteSpace(ownAppId))
{
  var ownServerId = await ResolveOwnServerIdAsync(
    app.Services.GetRequiredService<CfgService.CfgServiceClient>(), ownAppId);
  app.Services.GetRequiredService<ClipCleaner>().OwnServerId = ownServerId;
}

app.MapGrpcService<FsServiceImpl>();
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
