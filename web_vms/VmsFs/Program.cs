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

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();
builder.Services.AddHostedService<CameraConfigSubscriber>();
builder.Services.AddHostedService<ClipCleaner>();
var postgresConnStr = BuildPostgresConnectionString();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnStr));
builder.Services.AddDbContextFactory<ClipsDbContext>(options =>
  options.UseNpgsql(postgresConnStr));
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton(_ =>
  new StorageService.StorageServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<CameraConfigCache>();
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
app.MapGrpcService<FsServiceImpl>();
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
  var dsn = Environment.GetEnvironmentVariable("VMS_FS_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");
  if (!string.IsNullOrWhiteSpace(dsn))
    return dsn;

  return $"Host={Require("POSTGRES_HOST")};Port={Require("POSTGRES_PORT")};" +
    $"Database={Require("POSTGRES_FS_DB")};Username={Require("POSTGRES_USER")};" +
    $"Password={Require("POSTGRES_PASSWORD")}";
}
