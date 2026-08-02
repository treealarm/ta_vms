using Dapr.Messaging.PublishSubscribe.Extensions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Net.Http.Headers;
using System.Text;
using VmsLog;
using VmsUtils;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("UTILS_VMS_PORT"), out var p)
  ? p
  : throw new InvalidOperationException("Required environment variable 'UTILS_VMS_PORT' must be a valid integer");

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID")
  ?? throw new InvalidOperationException("Required environment variable 'DAPR_CFG_APP_ID' is not set");

// MediaMTX's Control API requires the vms_read service account (see init_files/config/mediamtx.yml).
// Until it did, these calls came from another container and were silently rejected by the old
// localhost-only api rule, which left reader-count polling — and with it media_server's lazy
// start/stop of WebRTC pushes — inert.
var mtxReadUser = Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_USER")
  ?? throw new InvalidOperationException("Required environment variable 'VMS_MEDIAMTX_READ_USER' is not set");
var mtxReadPass = Environment.GetEnvironmentVariable("VMS_MEDIAMTX_READ_PASS")
  ?? throw new InvalidOperationException("Required environment variable 'VMS_MEDIAMTX_READ_PASS' is not set");

builder.Services.AddGrpc();
builder.Services.AddHttpClient(UtilsServiceImpl.MediaMtxHttpClientName, c =>
  c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{mtxReadUser}:{mtxReadPass}"))));
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
