using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using VmsCfg;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton(_ =>
  NpgsqlDataSource.Create(BuildPostgresConnectionString()));
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<CameraStorage>();
builder.Services.AddSingleton<CfgServiceImpl>();

var grpc_port = 5135;
if (int.TryParse(Environment.GetEnvironmentVariable("CFG_VMS_PORT"), out grpc_port))
{
  Console.WriteLine($"CFG_VMS_PORT port: {grpc_port}");
}
else
{
  grpc_port = 5135;
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpc_port, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();
app.MapGrpcService<CfgServiceImpl>();
app.Run();

static string BuildPostgresConnectionString()
{
  var explicitDsn =
    Environment.GetEnvironmentVariable("VMS_CFG_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");

  if (!string.IsNullOrWhiteSpace(explicitDsn))
  {
    return explicitDsn;
  }

  var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
  var database = Environment.GetEnvironmentVariable("POSTGRES_CFG_DB") ?? "ta_cfg";
  var username = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "postgres";
  var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "postgres";
  var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";

  return $"Host={host};Port={port};Database={database};Username={username};Password={password}";
}
