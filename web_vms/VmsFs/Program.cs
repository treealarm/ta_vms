using Dapr.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using Vms.Cfg;
using VmsFs;

var builder = WebApplication.CreateBuilder(args);

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID") ?? "vmscfg";

builder.Services.AddGrpc();
builder.Services.AddSingleton(_ =>
  NpgsqlDataSource.Create(BuildPostgresConnectionString()));
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<CameraConfigCache>();
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<ClipStorage>();
builder.Services.AddSingleton<FsServiceImpl>();

var grpcPort = 5137;
if (int.TryParse(Environment.GetEnvironmentVariable("FS_VMS_PORT"), out grpcPort))
{
  Console.WriteLine($"FS_VMS_PORT port: {grpcPort}");
}
else
{
  grpcPort = 5137;
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();
app.MapGrpcService<FsServiceImpl>();
app.Run();

static string BuildPostgresConnectionString()
{
  var explicitDsn =
    Environment.GetEnvironmentVariable("VMS_FS_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");

  if (!string.IsNullOrWhiteSpace(explicitDsn))
  {
    return explicitDsn;
  }

  var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
  var database = Environment.GetEnvironmentVariable("POSTGRES_FS_DB") ?? "ta_fs";
  var username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
  var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
  var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";

  return $"Host={host};Port={port};Database={database};Username={username};Password={password}";
}
