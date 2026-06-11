using Dapr.Client;
using System.Net;
using Vms.Cfg;
using Vms.Onvif;
using Vms.Utils;
using web_vms;
using Vms.Logger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var loggerAppId = Require("DAPR_LOGGER_APP_ID");
builder.Services.AddSingleton(_ =>
  new LogService.LogServiceClient(
    DaprClient.CreateInvocationInvoker(loggerAppId)));

var cfgAppId = Require("DAPR_CFG_APP_ID");
builder.Services.AddSingleton(_ =>
  new CfgService.CfgServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));
builder.Services.AddSingleton(_ =>
  new StorageService.StorageServiceClient(
    DaprClient.CreateInvocationInvoker(cfgAppId)));

builder.Services.AddSingleton<ServerPairRegistry>();

var onvifAppId = Require("DAPR_ONVIF_APP_ID");
builder.Services.AddSingleton(_ =>
  new OnvifService.OnvifServiceClient(DaprClient.CreateInvocationInvoker(onvifAppId)));
builder.Services.AddSingleton(_ =>
  new PtzService.PtzServiceClient(DaprClient.CreateInvocationInvoker(onvifAppId)));

var utilsAppId = Require("DAPR_UTILS_APP_ID");
builder.Services.AddSingleton(_ =>
  new UtilsService.UtilsServiceClient(DaprClient.CreateInvocationInvoker(utilsAppId)));


builder.Services.AddSingleton<ICameraManager, CameraManager>();
builder.Services.AddSingleton<MonitorManager>();
builder.Services.AddSingleton<IHlsArchiveService, HlsArchiveService>();
builder.Services.AddSingleton<IArchiveTsStreamer, ArchiveTsStreamer>();
builder.Services.AddSingleton<StreamStateService>();

var http_port = RequirePort("WEB_VMS_PORT");

static string Require(string name) =>
  Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Required environment variable '{name}' is not set");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");

builder.WebHost.ConfigureKestrel(options =>
{
  options.Listen(IPAddress.Any, http_port);
});

var app = builder.Build();

var registry = app.Services.GetRequiredService<ServerPairRegistry>();
await RetryAsync(() => registry.InitAsync());

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

if (app.Environment.IsDevelopment())
{
  app.UseSwagger();
  app.UseSwaggerUI();
}

app.UseAuthorization();
app.UseMiddleware<HeadRequestMiddleware>();

app.MapControllers();

app.MapFallbackToFile("/index.html");

app.Run();

static async Task RetryAsync(Func<Task> action)
{
  while (true)
  {
    try { await action(); return; }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Startup init failed, retrying in 5s: {ex.Message}");
      await Task.Delay(5_000);
    }
  }
}
