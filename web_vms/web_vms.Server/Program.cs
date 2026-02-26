using Grpc.Net.Client;
using Vms.Control;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var grpcAddress = builder.Configuration.GetValue<string>("VmsGrpc:Address") ?? "http://localhost:50051";
var grpcChannel = GrpcChannel.ForAddress(grpcAddress);
builder.Services.AddSingleton(grpcChannel);
builder.Services.AddSingleton(_ => new CameraControlService.CameraControlServiceClient(grpcChannel));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();
