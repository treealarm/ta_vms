using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using Vms.Map;
using VmsLog;
using VmsSingleton;
using VmsSingleton.EventRouting;

// The one process in the system that must never be replicated — see CLAUDE.md. It holds work that
// has to happen exactly once (periodic jobs over global state) and in-memory aggregation that a
// second instance would split rather than share: all replicas of an app id share one Dapr consumer
// group, so a second copy would receive half the events and answer with half the picture.
var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();
// The event router's failed and suppressed runs are reported here; without it the diagnostics the
// plan promises have nowhere to go.
builder.AddVmsDebugState();

var grpcPort = RequirePort("SINGLETON_VMS_PORT");

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();
builder.Services.AddSingleton<EntityStateRegistry>();
builder.Services.AddHostedService<EntityStateSubscriber>();
builder.Services.AddHostedService<EntityStateSweepService>();
builder.Services.AddSingleton<SingletonServiceImpl>();

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));

// ── Event routing ───────────────────────────────────────────────────────────────────────────────
// What reacts to a journal event, and the state of the reactions already under way. VmsSingleton is
// where this lives because a run must happen in exactly one place (see CLAUDE.md); everything it
// remembers between messages is in the shared state store, not in this process, so a restart
// resumes rather than forgets.
var mapAppId = Require("DAPR_MAP_APP_ID");
var stateStoreName = Require("DAPR_STATE_STORE_NAME");

builder.Services.AddSingleton(_ =>
  new MapService.MapServiceClient(DaprClient.CreateInvocationInvoker(mapAppId)));

builder.Services.AddSingleton<IMapGateway, MapGateway>();
builder.Services.AddSingleton<MediaServerRegistry>();
builder.Services.AddSingleton<INodeActions, NodeActions>();
builder.Services.AddSingleton<IRunStore>(sp => new DaprEventRunStore(
  sp.GetRequiredService<DaprClient>(), stateStoreName));
builder.Services.AddSingleton<IRoutingDiagnostics, EntityStateRoutingDiagnostics>();

// Registered as itself as well as a hosted service: the router reads it, and the hosted half is
// the subscriber that invalidates it.
builder.Services.AddSingleton<SubscriptionCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SubscriptionCache>());

builder.Services.AddSingleton<ActionExecutor>();
builder.Services.AddSingleton<EventRouter>();
builder.Services.AddHostedService<EventRouterSubscriber>();

// The schedule ticker: one window boundary must produce one announcement, which is exactly the
// guarantee this service exists to give. Evaluation stays in VmsCfg — see RecordingScheduleService.
builder.Services.AddSingleton<ScheduleChangeSignal>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleChangeSignal>());
builder.Services.AddHostedService<RecordingScheduleService>();
builder.Services.AddHostedService<AlarmOverrideExpiryService>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenLocalhost(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<SingletonServiceImpl>();
app.Run();

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");
