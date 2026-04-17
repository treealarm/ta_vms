using Microsoft.AspNetCore.Server.Kestrel.Core;
using VmsCfg;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<CameraStorage>();
builder.Services.AddSingleton<CfgServiceImpl>();

var grpc_port = 5135;
if (int.TryParse(Environment.GetEnvironmentVariable("CFG_VMS_PORT"), out grpc_port))
{
  Console.WriteLine($"CFG_VMS_PORT port: {grpc_port}");
}
else
{
  grpc_port = 5135;
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpc_port, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
app.MapGrpcService<CfgServiceImpl>();
app.Run();
