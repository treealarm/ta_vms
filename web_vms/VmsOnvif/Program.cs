using Dapr.Client;
using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using VmsLog;
using VmsOnvif;
using web_vms.Types.Actors;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

builder.Services.AddGrpc();
builder.Services.AddSingleton(_ => new DaprClientBuilder().Build());
builder.Services.AddDaprPubSubClient();

// This instance serves exactly one node: ONVIF reaches a camera over the same local subnet RTSP
// does, so it can only work with the cameras assigned to the media server it sits next to.
var ownServerId = Require("OWN_SERVER_ID");
// Rejected at startup rather than on the first PTZ command: the value becomes part of the Dapr
// actor type name, and validating it lazily would let the service come up looking healthy and then
// fail every camera call.
var cameraActorType = CameraActorType.For(ownServerId);

builder.Services.AddSingleton<CameraConnCache>();
builder.Services.AddSingleton(sp => new OnvifServiceImpl(
  sp.GetRequiredService<CameraConnCache>(),
  sp.GetRequiredService<OnvifEventSubscriptionService>(),
  sp.GetRequiredService<ObservedTopicsStore>(),
  sp.GetRequiredService<OnvifSettingsService>(),
  cameraActorType,
  sp.GetRequiredService<ILogger<OnvifServiceImpl>>()));
builder.Services.AddSingleton(sp => new PtzServiceImpl(
  sp.GetRequiredService<CameraConnCache>(),
  sp.GetRequiredService<OnvifSettingsService>(),
  cameraActorType));
builder.Services.AddSingleton<DeviceIoServiceImpl>();

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));

builder.Services.AddSingleton(sp =>
  new ServerPairRegistry(sp.GetRequiredService<CfgService.CfgServiceClient>(), ownServerId));

builder.Services.AddSingleton<OnvifSettingsService>();
builder.Services.AddSingleton<OnvifFilterCache>();
builder.Services.AddSingleton<CameraArmedCache>();
builder.Services.AddSingleton<ObservedTopicsStore>();
builder.Services.AddSingleton(sp => new OnvifEventSubscriptionService(
  sp.GetRequiredService<CfgService.CfgServiceClient>(),
  sp.GetRequiredService<ServerPairRegistry>(),
  sp.GetRequiredService<OnvifFilterCache>(),
  sp.GetRequiredService<CameraArmedCache>(),
  sp.GetRequiredService<ObservedTopicsStore>(),
  ownServerId,
  sp.GetRequiredService<ILogger<OnvifEventSubscriptionService>>()));
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

var registry = app.Services.GetRequiredService<ServerPairRegistry>();
await RetryAsync(() => registry.InitAsync());

app.MapGrpcService<OnvifServiceImpl>();
app.MapGrpcService<PtzServiceImpl>();
app.MapGrpcService<DeviceIoServiceImpl>();
app.Run();

static async Task RetryAsync(Func<Task> action)
{
  while (true)
  {
    try { await action(); return; }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Startup init failed, retrying in 5s: {ex.Message}");
      await Task.Delay(5_000);
    }
  }
}
