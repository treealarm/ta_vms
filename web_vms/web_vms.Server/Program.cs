using Dapr.Client;
using KeycloakAdmin;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vms.Cfg;
using Vms.Onvif;
using Vms.Utils;
using web_vms;
using web_vms.Authentication;
using web_vms.Authorization;
using Vms.Logger;

var builder = WebApplication.CreateBuilder(args);

// CameraAccessFilter is global on purpose: it is what guarantees no endpoint reaches video without
// having declared which permission it needs (see the fail-closed branch in the filter).
builder.Services.AddControllers(o =>
{
  o.Filters.Add<RpcExceptionFilter>();
  o.Filters.Add<CameraAccessFilter>();
});
builder.Services.AddSingleton<ICameraAccessService, CameraAccessService>();
// State store access only: the gateway publishes nothing and subscribes to nothing. Permission
// freshness is a shared version key it polls (see CameraAccessService) precisely because a
// subscription would reach one replica out of N.
builder.Services.AddDaprClient();
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
builder.Services.AddSingleton<CfgHealthProbe>();
// Loaded in the background so the gateway serves (and can explain itself via /api/health) even
// while VmsCfg is unavailable — see ServerPairInitializer.
builder.Services.AddHostedService<ServerPairInitializer>();

var onvifAppId = Require("DAPR_ONVIF_APP_ID");
builder.Services.AddSingleton(_ =>
  new OnvifService.OnvifServiceClient(DaprClient.CreateInvocationInvoker(onvifAppId)));
builder.Services.AddSingleton(_ =>
  new PtzService.PtzServiceClient(DaprClient.CreateInvocationInvoker(onvifAppId)));
builder.Services.AddSingleton(_ =>
  new DeviceIoService.DeviceIoServiceClient(DaprClient.CreateInvocationInvoker(onvifAppId)));

var utilsAppId = Require("DAPR_UTILS_APP_ID");
builder.Services.AddSingleton(_ =>
  new UtilsService.UtilsServiceClient(DaprClient.CreateInvocationInvoker(utilsAppId)));

var analyticsAppId = Require("DAPR_ANALYTICS_APP_ID");
builder.Services.AddSingleton(_ =>
  new Vms.Analytics.VmsAnalyticsService.VmsAnalyticsServiceClient(
    DaprClient.CreateInvocationInvoker(analyticsAppId)));

builder.Services.AddSingleton<ICameraManager, CameraManager>();
builder.Services.AddSingleton<IPermissionManager, PermissionManager>();
builder.Services.AddSingleton<MonitorManager>();
builder.Services.AddSingleton<ArchiveOwnershipCache>();
builder.Services.AddSingleton<IHlsArchiveService, HlsArchiveService>();
builder.Services.AddSingleton<IArchiveTsStreamer, ArchiveTsStreamer>();
builder.Services.AddSingleton<StreamStateService>();

// MediaMTX has no anonymous access: the gateway authenticates with the vms_read service account
// on the browser's behalf. The user's JWT is checked by us and never travels to MediaMTX — these
// are two separate credential worlds, and the browser never sees this one.
var mtxBasic = Convert.ToBase64String(
  Encoding.UTF8.GetBytes($"{Require("VMS_MEDIAMTX_READ_USER")}:{Require("VMS_MEDIAMTX_READ_PASS")}"));

builder.Services.AddHttpClient(WhepProxyService.MediaMtxHttpClientName, c =>
{
  c.Timeout = TimeSpan.FromSeconds(10);
  c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", mtxBasic);
});

// Sealing key for WHEP session tokens. Required rather than generated per process on purpose: a
// per-process key would work perfectly on one instance and quietly break session teardown the day
// a second replica is added, which is the failure this token format exists to remove.
builder.Services.AddSingleton(WhepSessionToken.FromBase64Key(Require("VMS_WHEP_SESSION_KEY")));
// Same key, different token type — see EdgeExportToken for why they share it.
builder.Services.AddSingleton(new EdgeExportToken(Require("VMS_WHEP_SESSION_KEY")));
builder.Services.AddSingleton<WhepProxyService>();

// KEYCLOAK_URL is intentionally optional: when unset, login/authorization is disabled entirely
// and web_vms keeps running standalone (no dependency on Square's Keycloak). When set, the
// remaining KEYCLOAK_* vars become required so misconfiguration fails fast at startup.
var keycloakUrl = Environment.GetEnvironmentVariable("KEYCLOAK_URL");

// VMS_LIVE_TEST_PLAYER opens an anonymous WHEP mirror (WhepTestController) that can play any
// camera by id — it bypasses login and every per-camera permission. Refusing to start makes the
// dangerous combination impossible to reach by accident instead of leaving a quiet hole.
if (!string.IsNullOrEmpty(keycloakUrl) && web_vms.Controllers.FrontendConfigController.LiveTestPlayerEnabled)
{
  throw new InvalidOperationException(
    "VMS_LIVE_TEST_PLAYER=true cannot be combined with KEYCLOAK_URL: it exposes an anonymous WHEP "
    + "endpoint for every camera. Unset VMS_LIVE_TEST_PLAYER in any deployment that has authentication.");
}

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
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AuthenticatedAuthorizationHandler>();
builder.Services.AddAuthorizationBuilder()
  .AddPolicy(AuthPolicies.AdminOnly, policy => policy.Requirements.Add(new AdminRoleRequirement()))
  .AddPolicy(AuthPolicies.Authenticated, policy => policy.Requirements.Add(new AuthenticatedRequirement()));

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
