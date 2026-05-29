using Microsoft.AspNetCore.Server.Kestrel.Core;
using VmsUtils;

var builder = WebApplication.CreateBuilder(args);

var grpcPort = int.TryParse(Environment.GetEnvironmentVariable("UTILS_VMS_PORT"), out var p)
  ? p
  : throw new InvalidOperationException("Required environment variable 'UTILS_VMS_PORT' must be a valid integer");

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID")
  ?? throw new InvalidOperationException("Required environment variable 'DAPR_CFG_APP_ID' is not set");

builder.Services.AddGrpc();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<UtilsServiceImpl>(sp => new UtilsServiceImpl(
  sp.GetRequiredService<IHttpClientFactory>(),
  cfgAppId));

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, lo => lo.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<UtilsServiceImpl>();
app.Run();
