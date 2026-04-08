using Grpc.Net.Client;
using System.Net;
using Vms.Control;
using web_vms;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddGrpc();

var grpcAddress = Environment.GetEnvironmentVariable("GRPC_VMS_ADDR");

if (!string.IsNullOrEmpty(grpcAddress))
{
  Console.WriteLine($"GRPC_VMS_ADDR: {grpcAddress}");
}
else
{
  grpcAddress = "http://localhost:50051";
}


var grpcChannel = GrpcChannel.ForAddress(grpcAddress);
builder.Services.AddSingleton(grpcChannel);
builder.Services.AddSingleton(_ => new CameraControlService.CameraControlServiceClient(grpcChannel));
builder.Services.AddSingleton<ICameraManager, CameraManager>();

var http_port = 5134;

if (int.TryParse(Environment.GetEnvironmentVariable("WEB_VMS_PORT"), out http_port))
{
  Console.WriteLine($"WEB_VMS_PORT port: {http_port}");
}
else
{
  http_port = 5134;
}

var grpc_port = 5135;

if (int.TryParse(Environment.GetEnvironmentVariable("CFG_VMS_PORT"), out grpc_port))
{
  Console.WriteLine($"CFG_VMS_PORT port: {5135}");
}
else
{
  grpc_port = 5135;
}


builder.WebHost.ConfigureKestrel(options =>
{
  options.Listen(IPAddress.Any, grpc_port, listenOptions =>
  {
    listenOptions.Protocols = HttpProtocols.Http2;
  });
  options.Listen(IPAddress.Any, http_port);
});

var app = builder.Build();

app.MapGrpcService<CfgServiceImpl>();


app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
