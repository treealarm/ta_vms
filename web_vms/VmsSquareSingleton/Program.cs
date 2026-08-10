using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SquareIntegrationClient;
using Vms.Cfg;
using VmsLog;
using VmsSquareSingleton;

// The link between ta_vms and Square, and nothing else. It exists as its own service because the
// integration is a translation layer coupled to another system's protocol and release cycle, and
// because it must run exactly once: a startup resync pushes every camera, and two copies would push
// the whole tree twice. See CLAUDE.md for the Singleton naming rule.
var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = RequirePort("SQUARE_SINGLETON_VMS_PORT");

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));

// SQUARE_INTEGRATION_SINK_APP_ID is intentionally optional: when unset the service starts and does
// nothing, so a ta_vms with no Square keeps working exactly as before (the autonomy invariant in
// CLAUDE.md). When set, it must name Square's receiving Dapr app-id — its integrationhost service,
// not the user-facing leafletalarms app.
var squareSinkAppId = Environment.GetEnvironmentVariable("SQUARE_INTEGRATION_SINK_APP_ID");
if (!string.IsNullOrEmpty(squareSinkAppId))
{
  // Our own identity in Square's tree, configured rather than derived — see SquareProducerId.
  var producerId = Require("SQUARE_INTEGRATION_PRODUCER_ID");
  builder.Services.AddSingleton(new SquareProducerId(producerId));
  builder.Services.AddSingleton<ISquareIntegration>(
    _ => new SquareIntegrationGrpcClient(squareSinkAppId, producerId));
  builder.Services.AddHostedService<CameraChangeSubscriber>();
  builder.Services.AddHostedService<SquareAlarmSubscriber>();
  builder.Services.AddHostedService<SquareResyncService>();
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.Run();

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");
