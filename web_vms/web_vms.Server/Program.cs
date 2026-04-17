using Grpc.Net.Client;
using System.Net;
using Vms.Control;
using Vms.Cfg;
using web_vms;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
builder.Services.AddSingleton(_ => new CameraControlService.CameraControlServiceClient(grpcChannel));

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

builder.WebHost.ConfigureKestrel(options =>
{
  options.Listen(IPAddress.Any, http_port);
});

var app = builder.Build();

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
