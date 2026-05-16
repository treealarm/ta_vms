using VmsOnvifActors;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddActors(options =>
{
  options.Actors.RegisterActor<CameraActor>();
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
