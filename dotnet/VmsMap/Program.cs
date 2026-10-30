using Dapr.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;
using VmsDb;
using VmsLog;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Grpc;
using VmsMap.PubSub;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = RequirePort("MAP_VMS_PORT");
var cfgAppId = Require("DAPR_CFG_APP_ID");
var pubSubName = Require("PUBSUB_NAME");
var stateStoreName = Require("DAPR_STATE_STORE_NAME");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();

// The camera core. A placement is checked against a real camera and the camera's node is named
// after it: cameras live in VmsCfg's database, so this is the check that replaced the foreign key.
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<ICameraCatalog, VmsCfgCameraCatalog>();
builder.Services.AddSingleton<IMapConfigNotifier>(sp => new DaprMapConfigNotifier(
  sp.GetRequiredService<DaprClient>(),
  sp.GetRequiredService<ILogger<DaprMapConfigNotifier>>(),
  pubSubName));
builder.Services.AddSingleton<IRightsVersionNotifier>(sp => new DaprRightsVersionNotifier(
  sp.GetRequiredService<DaprClient>(),
  sp.GetRequiredService<ILogger<DaprRightsVersionNotifier>>(),
  stateStoreName));

var postgresConnStr = BuildPostgresConnectionString();

// Migrations run on a data source of their own, disposed before the service's one is built.
// Npgsql loads the database's types when a data source first connects, and PostGIS does not exist
// until the migration creates it: a data source that connected first would keep a type catalog
// without the geometry types, and the first figure or footprint write would fail until a restart.
await using (var migrationSource = new NpgsqlDataSourceBuilder(postgresConnStr).Build())
{
  await new VmsDbMigrator(migrationSource, "VmsMap", builder.Environment.ContentRootPath, "7743512881")
    .MigrateAsync();
}

builder.Services.AddSingleton(_ =>
{
  // One data source for the process, not one per request as Square built it.
  var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnStr);
  // db_values.value and the event payloads are jsonb read into JsonDocument.
  dataSourceBuilder.EnableDynamicJson();
  dataSourceBuilder.UseNetTopologySuite();
  return dataSourceBuilder.Build();
});

builder.Services.AddDbContext<VmsMapDbContext>((sp, options) =>
  options.UseNpgsql(
    sp.GetRequiredService<NpgsqlDataSource>(),
    npgsqlOptions => npgsqlOptions.UseNetTopologySuite()));

// Repositories. They share the scoped context, so a transaction opened by the gRPC layer covers
// every SaveChanges its steps make.
builder.Services.AddScoped<IMapService, MapDb>();
builder.Services.AddScoped<IStateService, StateService>();
builder.Services.AddScoped<IEventsService, EventsService>();
builder.Services.AddScoped<IGeoService, GeoService>();
builder.Services.AddScoped<ITrackService, TrackService>();
builder.Services.AddScoped<DiagramService>();
builder.Services.AddScoped<IDiagramService>(sp => sp.GetRequiredService<DiagramService>());
builder.Services.AddScoped<IDiagramServiceInternal>(sp => sp.GetRequiredService<DiagramService>());
builder.Services.AddScoped<IDiagramTypeService, DiagramTypeService>();
builder.Services.AddScoped<ValuesService>();
builder.Services.AddScoped<IValuesService>(sp => sp.GetRequiredService<ValuesService>());
builder.Services.AddScoped<IValuesServiceInternal>(sp => sp.GetRequiredService<ValuesService>());
builder.Services.AddScoped<ILevelService, LevelService>();
builder.Services.AddScoped<RightService>();
builder.Services.AddScoped<IRightServiceInternal>(sp => sp.GetRequiredService<RightService>());
builder.Services.AddScoped<IRightsService, RightsService>();
builder.Services.AddScoped<VisualService>();
builder.Services.AddScoped<FileService>();

// Spaces, plans, placements, landmarks and observations on the one tree.
builder.Services.AddScoped<SpatialDb>();

builder.Services.AddScoped<ITracksUpdateService, TracksUpdateService>();
builder.Services.AddScoped<IMapUpdateService, MapUpdateService>();
builder.Services.AddScoped<IStatesUpdateService, StatesUpdateService>();

builder.Services.AddScoped<MapServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenLocalhost(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();

app.MapGrpcService<MapServiceImpl>();

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
  var dsn = Environment.GetEnvironmentVariable("VMS_MAP_POSTGRES_DSN")
    ?? Environment.GetEnvironmentVariable("POSTGRES_DSN");
  if (!string.IsNullOrWhiteSpace(dsn))
    return dsn;

  return $"Host={Require("POSTGRES_HOST")};Port={Require("POSTGRES_PORT")};" +
    $"Database={Require("POSTGRES_MAP_DB")};Username={Require("POSTGRES_USER")};" +
    $"Password={Require("POSTGRES_PASSWORD")}";
}
