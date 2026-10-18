using Microsoft.AspNetCore.Server.Kestrel.Core;
using VmsLog;
using VmsLogger;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

builder.Services.AddGrpc();
builder.Services.AddSingleton<LogServiceImpl>();
builder.Services.AddHostedService<LogRetentionService>();

if (!int.TryParse(Environment.GetEnvironmentVariable("LOGGER_VMS_PORT"), out var grpcPort))
  throw new InvalidOperationException("Required environment variable 'LOGGER_VMS_PORT' must be a valid integer");

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
