using Microsoft.AspNetCore.Server.Kestrel.Core;
using VmsLogger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<LogServiceImpl>();

var grpcPort = 5138;
if (int.TryParse(Environment.GetEnvironmentVariable("LOGGER_VMS_PORT"), out grpcPort))
{
  Console.WriteLine($"LOGGER_VMS_PORT port: {grpcPort}");
}
else
{
  grpcPort = 5138;
}

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(grpcPort, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
});

var app = builder.Build();
app.MapGrpcService<LogServiceImpl>();
app.Run();
