using Dapr.Client;
using KeycloakAdmin;
using System.Net;
using Vms.Cfg;
using Vms.Onvif;
using Vms.Utils;
using web_vms;
using web_vms.Authentication;
using Vms.Logger;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<RpcExceptionFilter>());
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

var analyticsAppId = Require("DAPR_ANALYTICS_APP_ID");
builder.Services.AddSingleton(_ =>
  new Vms.Analytics.VmsAnalyticsService.VmsAnalyticsServiceClient(
    DaprClient.CreateInvocationInvoker(analyticsAppId)));

builder.Services.AddSingleton<ICameraManager, CameraManager>();
builder.Services.AddSingleton<MonitorManager>();
builder.Services.AddSingleton<IHlsArchiveService, HlsArchiveService>();
builder.Services.AddSingleton<IArchiveTsStreamer, ArchiveTsStreamer>();
builder.Services.AddSingleton<StreamStateService>();

builder.Services.AddHttpClient(WhepProxyService.MediaMtxHttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<WhepProxyService>();

// KEYCLOAK_URL is intentionally optional: when unset, login/authorization is disabled entirely
// and web_vms keeps running standalone (no dependency on Square's Keycloak). When set, the
// remaining KEYCLOAK_* vars become required so misconfiguration fails fast at startup.
var keycloakUrl = Environment.GetEnvironmentVariable("KEYCLOAK_URL");
if (!string.IsNullOrEmpty(keycloakUrl))
{
  var keycloakAdminUser = Require("KEYCLOAK_ADMIN_USER");
  var keycloakAdminPassword = Require("KEYCLOAK_ADMIN_PASSWORD");
  var keycloakRealmName = Require("KEYCLOAK_REALM_NAME");
  _ = Require("KEYCLOAK_CLIENT_ID");

  builder.Services.AddSingleton<IKeycloakAdminClient>(_ =>
    new KeycloakAdminClient(keycloakUrl, keycloakAdminUser, keycloakAdminPassword));
  builder.Services.AddKeycloakAuth(keycloakUrl, keycloakRealmName);
}

// Registered unconditionally: see AdminRoleAuthorizationHandler for the KEYCLOAK_URL gate.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminRoleAuthorizationHandler>();
builder.Services.AddAuthorizationBuilder()
  .AddPolicy(AuthPolicies.AdminOnly, policy => policy.Requirements.Add(new AdminRoleRequirement()));

// SQUARE_FRONTEND_ORIGIN is intentionally optional: when unset, no CORS policy is configured and
// cross-origin requests are rejected by the browser as before — web_vms stays standalone. When
// set, it enables Square's Monitor tab to call this API directly from its own origin (both apps
// trust the same Keycloak realm, so a Square-issued bearer token validates here too).
const string SquareCorsPolicy = "SquareFrontend";
var squareFrontendOrigin = Environment.GetEnvironmentVariable("SQUARE_FRONTEND_ORIGIN");
if (!string.IsNullOrEmpty(squareFrontendOrigin))
{
  builder.Services.AddCors(options =>
    options.AddPolicy(SquareCorsPolicy, policy =>
      policy.WithOrigins(squareFrontendOrigin).AllowAnyHeader().AllowAnyMethod()
        // 'Location' isn't in the CORS-safelisted response headers, but the WHEP offer response
        // carries the session-cleanup URL in it (see WhepResultMapper) — without this, cross-origin
        // callers can't read it via resp.headers.get('Location') and WHEP sessions never get
        // explicitly closed.
        .WithExposedHeaders("Location")));
}

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

if (!string.IsNullOrEmpty(squareFrontendOrigin))
{
  app.UseCors(SquareCorsPolicy);
}

if (!string.IsNullOrEmpty(keycloakUrl))
{
  app.UseAuthentication();
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
