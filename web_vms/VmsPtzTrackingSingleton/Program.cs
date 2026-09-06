using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Analytics;
using Vms.Cfg;
using VmsLog;
using VmsPtzTrackingSingleton;
using VmsPtzTrackingSingleton.Grpc;

// Keeps a PTZ camera pointed at an object the analytics found.
//
// Its own service because it must run exactly once for the whole deployment: a second copy would
// have two control loops sending one motor opposing commands. See CLAUDE.md for the Singleton
// naming rule, and the feature plan for why the loop is a plain task per camera rather than a Dapr
// actor — briefly, the detection stream needs a single consumer either way, so actors would be
// added to the singleton rather than replacing it.
var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();
builder.AddVmsDebugState();

var grpcPort = RequirePort("PTZ_TRACKING_VMS_PORT");

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));

var analyticsAppId = Require("DAPR_ANALYTICS_APP_ID");
builder.Services.AddSingleton(_ =>
  new VmsAnalyticsService.VmsAnalyticsServiceClient(DaprClient.CreateInvocationInvoker(analyticsAppId)));

builder.Services.AddSingleton<CameraPtzRouter>();
builder.Services.AddSingleton<PtzCalibrationCache>();
builder.Services.AddSingleton<TrackingPolicyCache>();
builder.Services.AddSingleton<IPtzCommandSender, PtzCommandSender>();
builder.Services.AddSingleton<DetectionStreamClient>();
builder.Services.AddSingleton<TrackingSessionManager>();
// Registered as itself as well as a hosted service: the config subscriber holds a reference to
// nudge it, and AddHostedService<T> alone would hand that reference a second, separate instance.
builder.Services.AddSingleton<TrackingReconciler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrackingReconciler>());
builder.Services.AddHostedService<CameraConfigSubscriber>();
builder.Services.AddHostedService<TrackingStatePublisher>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<PtzTrackingServiceImpl>();
app.Run();

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");
