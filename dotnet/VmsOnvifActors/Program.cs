using VmsLog;
using VmsOnvifActors;
using web_srv.Types.Actors;

var builder = WebApplication.CreateBuilder(args);
builder.AddVmsLogging();

// Registered under a node-scoped type name so placement can only put a camera's actor on a host
// that shares its subnet — see CameraActorType.
var ownServerId = Environment.GetEnvironmentVariable("OWN_SERVER_ID")
  ?? throw new InvalidOperationException("Required environment variable 'OWN_SERVER_ID' is not set");
var actorTypeName = CameraActorType.For(ownServerId);

builder.Services.AddSingleton(new CameraActorDomain(ownServerId));
builder.Services.AddActors(options =>
{
  options.Actors.RegisterActor<CameraActor>(actorTypeName, _ => { });
});

var httpPort = RequirePort("ONVIF_ACTORS_PORT");

static int RequirePort(string name) =>
  int.TryParse(Environment.GetEnvironmentVariable(name), out var p)
    ? p
    : throw new InvalidOperationException($"Required environment variable '{name}' must be a valid integer");

builder.WebHost.ConfigureKestrel(options =>
{
  options.ListenAnyIP(httpPort);
});

var app = builder.Build();
app.MapActorsHandlers();
app.Run();
 