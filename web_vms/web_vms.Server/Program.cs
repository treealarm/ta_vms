using Grpc.Net.Client;
using System.Net;
using Vms.Control;

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
builder.Services.AddSingleton(grpcChannel);
builder.Services.AddSingleton(_ => new CameraControlService.CameraControlServiceClient(grpcChannel));

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
  // this configuration is the same as:
  //- ASPNETCORE_URLS=http://+:8000;http://+:${GRPC_MAIN_PORT}
  //- Kestrel__Endpoints__gRPC__Url=http://*:${GRPC_MAIN_PORT}
  //- Kestrel__Endpoints__gRPC__Protocols=Http2
  //- Kestrel__Endpoints__Http__Url=http://*:8000
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
