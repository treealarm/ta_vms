using Microsoft.AspNetCore.Server.Kestrel.Core;
using Npgsql;
using VmsCfg;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = RequirePort("CFG_VMS_PORT");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(BuildPostgresConnectionString()));
builder.Services.AddSingleton<DbMigrator>();
builder.Services.AddSingleton<CameraStorage>();
builder.Services.AddSingleton<StorageDb>();
builder.Services.AddSingleton<CfgServiceImpl>();
builder.Services.AddSingleton<StorageServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
await app.Services.GetRequiredService<DbMigrator>().MigrateAsync();
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
