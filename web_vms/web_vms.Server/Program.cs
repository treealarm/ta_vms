using Dapr.Client;
using System.Net;
using Vms.Cfg;
using web_vms;
using Vms.Logger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var loggerAppId = Environment.GetEnvironmentVariable("DAPR_LOGGER_APP_ID") ?? "vms";
Console.WriteLine($"DAPR_LOGGER_APP_ID: {loggerAppId}");
builder.Services.AddSingleton(_ =>
  new LogService.LogServiceClient(
    DaprClient.CreateInvocationInvoker(loggerAppId)));

var cfgAppId = Environment.GetEnvironmentVariable("DAPR_CFG_APP_ID") ?? "vmscfg";
Console.WriteLine($"DAPR_CFG_APP_ID: {cfgAppId}");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));

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
