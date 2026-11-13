using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Cfg;
using VmsDb;
using VmsLog;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Demo;
using VmsMap.Domain;
using VmsMap.Grpc;
using VmsMap.Outbox;
using VmsMap.PubSub;
using VmsMap.Watch;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = RequirePort("MAP_VMS_PORT");
var cfgAppId = Require("DAPR_CFG_APP_ID");
var pubSubName = Require("PUBSUB_NAME");
var stateStoreName = Require("DAPR_STATE_STORE_NAME");

builder.Services.AddGrpc();
builder.Services.AddDaprClient();
builder.Services.AddDaprPubSubClient();

// The camera core. A placement is checked against a real camera and the camera's node is named
// after it: cameras live in VmsCfg's database, so this is the check that replaced the foreign key.
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<ICameraCatalog, VmsCfgCameraCatalog>();
builder.Services.AddSingleton<ICameraStateSource, VmsCfgCameraStateSource>();
builder.Services.AddSingleton<IMediaServerRegistry, VmsCfgMediaServerRegistry>();
builder.Services.AddSingleton<IRetentionEventSource, VmsFsRetentionEventSource>();
builder.Services.AddSingleton<ICameraConnectionSource, VmsFsCameraConnectionSource>();
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
// The follower runs on a scope of its own; SpatialDb stays scoped with the context it shares.
builder.Services.AddScoped<ICameraGeoWriter>(sp => sp.GetRequiredService<SpatialDb>());
// The follower asks the camera where it stands through this one ONVIF client cache.
builder.Services.AddSingleton<VmsOnvifCameraSource>();
builder.Services.AddSingleton<ICameraGeoSource>(sp => sp.GetRequiredService<VmsOnvifCameraSource>());

builder.Services.AddSingleton(TimeProvider.System);

// The demo world: install, reinstall and removal behind one registry. The flag and the gRPC calls
// that reach it are wired in M2.2's later steps; the seeder is the same for both.
builder.Services.AddScoped<DemoSeeder>();
// `DEMO_CONFIG` names a world to install at startup; empty means none.
builder.Services.AddHostedService<DemoInstallService>();

builder.Services.AddScoped<ITracksUpdateService, TracksUpdateService>();
builder.Services.AddScoped<IMapUpdateService, MapUpdateService>();
builder.Services.AddScoped<IStatesUpdateService, StatesUpdateService>();

// The adapter: camera nodes from the config events, one full reconcile at startup, retention
// events into ALARM and the journal, and the sweep that takes a finished alarm away.
builder.Services.AddScoped<DeviceNodeService>();
builder.Services.AddScoped<IDeviceNodeService>(sp => sp.GetRequiredService<DeviceNodeService>());
// A device's diagram, drawn from a template for its model: one transaction and one advisory lock
// per device, because a snapshot and a configuration change reconcile it independently.
builder.Services.AddScoped<DeviceDiagramService>();
builder.Services.AddScoped<IDeviceDiagramService>(sp => sp.GetRequiredService<DeviceDiagramService>());
// Device settings are cached per camera, and the pub/sub signal reaches one replica only: the
// shared version key is the truth, the event only makes the cache notice sooner.
builder.Services.AddSingleton(sp => new DeviceSettingsCache(
  sp.GetRequiredService<CfgService.CfgServiceClient>(),
  sp.GetRequiredService<DaprClient>(),
  stateStoreName,
  sp.GetRequiredService<ILogger<DeviceSettingsCache>>()));
builder.Services.AddSingleton<IDeviceSettingsSource>(sp => sp.GetRequiredService<DeviceSettingsCache>());
// The definitions an alarm is raised under, cached by the same version key the subscriptions will
// use: event-subscriptions-changed reaches one replica, the shared key reaches them all.
builder.Services.AddSingleton(sp => new AlarmDefinitionsCache(
  sp.GetRequiredService<CfgService.CfgServiceClient>(),
  sp.GetRequiredService<DaprClient>(),
  stateStoreName,
  sp.GetRequiredService<ILogger<AlarmDefinitionsCache>>()));
builder.Services.AddSingleton<IAlarmDefinitionsSource>(sp => sp.GetRequiredService<AlarmDefinitionsCache>());
// The gate that decides whether an analytics detection becomes a journal row. A loaded list
// survives VmsCfg and the state store being unavailable; with none ever loaded it stays closed.
builder.Services.AddSingleton(sp => new AnalyticsSubscriptionGate(
  sp.GetRequiredService<CfgService.CfgServiceClient>(),
  sp.GetRequiredService<DaprClient>(),
  stateStoreName,
  sp.GetRequiredService<ILogger<AnalyticsSubscriptionGate>>()));

builder.Services.AddScoped<ObjectStateWriter>();
builder.Services.AddScoped<EventsWriter>();
builder.Services.AddScoped<DeviceEventTranslator>();
// The contact-to-alarm rule, shared by the event, the snapshot and a settings change.
builder.Services.AddScoped<SensorAlarmService>();
builder.Services.AddScoped<DeviceEventWriter>();
builder.Services.AddScoped<DeviceEventReconciler>();
// The behaviour of a logical device object — its arming mode and its contact rule — kept in the
// map, because the map is the only consumer.
builder.Services.AddScoped<VmsMap.Domain.Arming.DeviceObjectSettingsStore>();
builder.Services.AddScoped<VmsMap.Domain.Arming.EffectiveArmingResolver>();
// The one write behind every guard switch: a device delegates to VmsCfg, a container keeps its
// row in `object_arming`.
builder.Services.AddScoped<VmsMap.Domain.Arming.NodeArmingService>();
// The flag a zone implies, written back to VmsCfg for the cameras that inherit it: VmsOnvif and the
// routing gateway read that flag and cannot see the map.
builder.Services.AddScoped<VmsMap.Domain.Arming.CameraArmingReconciler>();
builder.Services.AddSingleton<ICfgArmingWriter, VmsCfgArmingWriter>();
// The operator's guarding list: a page of sensors with their effective arming, resolved in one
// pass rather than one camera at a time.
builder.Services.AddScoped<VmsMap.Domain.Control.ControlObjectQuery>();
builder.Services.AddScoped<CameraStateWriter>();
// The alarm entity: transitions, the ALARM state and the lifecycle events in one transaction.
builder.Services.AddScoped<AlarmStore>();
builder.Services.AddScoped<AlarmService>();
builder.Services.AddScoped<ConnectionStateService>();
builder.Services.AddScoped<WatchSnapshotService>();
builder.Services.AddSingleton<MapWatchPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MapWatchPoller>());
builder.Services.AddHostedService<CameraChangeSubscriber>();
builder.Services.AddHostedService<StartupResyncService>();
builder.Services.AddHostedService<RetentionEventSubscriber>();
// The definitions the alarm store raises under; the subscription only shortens the cache's clock.
builder.Services.AddHostedService<EventSubscriptionsSubscriber>();
// Device events, snapshots and the settings that name the parts.
builder.Services.AddHostedService<DeviceEventSubscriber>();
// A zone's arming changed: the sensors already at their trigger must raise now, from the outbox
// row written in the same transaction as the property.
builder.Services.AddHostedService<ArmingChangedSubscriber>();
// Persisted detections become journal rows, but only while a subscription asks for them.
builder.Services.AddHostedService<AnalyticsEventSubscriber>();
// Event notification goes through a transactional outbox: the row is written with the event, so a
// crash before the publish cannot lose the reaction.
builder.Services.AddHostedService(sp => new OutboxPublisher(
  sp.GetRequiredService<IServiceScopeFactory>(),
  sp.GetRequiredService<DaprClient>(),
  pubSubName,
  sp.GetRequiredService<ILogger<OutboxPublisher>>()));
builder.Services.AddHostedService<AlarmSweepService>();
builder.Services.AddHostedService<ConnectionStatePoller>();
// Cameras flagged geo_from_device follow their own ONVIF position; the rest are never asked.
builder.Services.AddHostedService<CameraGeoFollower>();

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
