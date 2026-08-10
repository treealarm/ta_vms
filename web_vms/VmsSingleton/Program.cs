using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using VmsLog;
using VmsSingleton;

// The one process in the system that must never be replicated — see CLAUDE.md. It holds work that
// has to happen exactly once (periodic jobs over global state) and in-memory aggregation that a
// second instance would split rather than share: all replicas of an app id share one Dapr consumer
// group, so a second copy would receive half the events and answer with half the picture.
var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

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

// The schedule ticker: one window boundary must produce one announcement, which is exactly the
// guarantee this service exists to give. Evaluation stays in VmsCfg — see RecordingScheduleService.
builder.Services.AddSingleton<ScheduleChangeSignal>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduleChangeSignal>());
builder.Services.AddHostedService<RecordingScheduleService>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
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
