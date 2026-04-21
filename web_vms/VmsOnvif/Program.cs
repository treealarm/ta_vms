using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Vms.Cfg;
using VmsOnvif;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<OnvifServiceImpl>();

var cfgGrpcAddress = Environment.GetEnvironmentVariable("CFG_VMS_ADDR");
if (!string.IsNullOrEmpty(cfgGrpcAddress))
{
  Console.WriteLine($"CFG_VMS_ADDR: {cfgGrpcAddress}");
}
else
{
  cfgGrpcAddress = "http://localhost:5135";
}

var cfgGrpcChannel = GrpcChannel.ForAddress(cfgGrpcAddress);
builder.Services.AddSingleton(_ => new CfgService.CfgServiceClient(cfgGrpcChannel));

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
