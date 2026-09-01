using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using VmsLog;
using VmsUtils;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("UTILS_VMS_PORT"), out var p)
  ? p
  : throw new InvalidOperationException("Required environment variable 'UTILS_VMS_PORT' must be a valid integer");

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID")
  ?? throw new InvalidOperationException("Required environment variable 'DAPR_CFG_APP_ID' is not set");

builder.Services.AddGrpc();
builder.Services.AddHttpClient(UtilsServiceImpl.MediaMtxHttpClientName);
builder.Services.AddDaprPubSubClient();
// One client for the lifetime of the process: the previous per-request construction churned a Dapr
// invocation channel on every reader-count poll.
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton<MediaMtxServerCache>();
builder.Services.AddSingleton<MediaMtxPathSnapshotCache>();
builder.Services.AddHostedService<MediaMtxConfigSubscriber>();
builder.Services.AddSingleton<UtilsServiceImpl>();

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<UtilsServiceImpl>();
app.Run();
