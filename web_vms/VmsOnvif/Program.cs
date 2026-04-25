using Dapr.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using VmsOnvif;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<OnvifServiceImpl>();

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID") ?? "vmscfg";
Console.WriteLine($"DAPR_CFG_APP_ID: {cfgAppId}");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));

var grpcPort = 5136;
if (int.TryParse(Environment.GetEnvironmentVariable("ONVIF_SRV_PORT"), out grpcPort))
{
  Console.WriteLine($"ONVIF_SRV_PORT port: {grpcPort}");
}
else
{
  grpcPort = 5136;
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
app.MapGrpcService<OnvifServiceImpl>();
app.Run();
