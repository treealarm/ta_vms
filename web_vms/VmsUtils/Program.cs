using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
builder.Services.AddHttpClient();
builder.Services.AddDaprPubSubClient();
builder.Services.AddSingleton<EntityStateRegistry>();
builder.Services.AddHostedService<EntityStateSubscriber>();
builder.Services.AddHostedService<EntityStateSweepService>();
builder.Services.AddSingleton<UtilsServiceImpl>(sp =>
  new UtilsServiceImpl(
    sp.GetRequiredService<IHttpClientFactory>(),
    cfgAppId,
    sp.GetRequiredService<EntityStateRegistry>(),
    sp.GetRequiredService<ILogger<UtilsServiceImpl>>()));

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<UtilsServiceImpl>();
app.Run();
