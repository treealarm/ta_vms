using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using Vms.Fs;
using VmsOnvif;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddDaprPubSubClient();
builder.Services.AddSingleton<OnvifServiceImpl>();
builder.Services.AddSingleton<PtzServiceImpl>();

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));

var fsAppId = Require("DAPR_FS_APP_ID");
builder.Services.AddSingleton(_ =>
  new FsService.FsServiceClient(
    DaprClient.CreateInvocationInvoker(fsAppId)));

builder.Services.AddSingleton<OnvifFilterCache>();
builder.Services.AddSingleton<OnvifEventSubscriptionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnvifEventSubscriptionService>());
builder.Services.AddHostedService<CameraConfigSubscriber>();

var grpcPort = RequirePort("ONVIF_SRV_PORT");

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
app.MapGrpcService<OnvifServiceImpl>();
app.MapGrpcService<PtzServiceImpl>();
app.Run();
