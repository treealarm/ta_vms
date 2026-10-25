using Dapr.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using System.Text;

namespace VmsLog;

public static class VmsLogExtensions
{
  /// The category the services give OnvifLib's logger. Its Debug is the whole SOAP envelope of every
  /// request, a few kilobytes each, so it is kept at Information below.
  public const string OnvifLibCategory = "OnvifLib";

  // Registers EntityStatePublisher for publishing debug "entity state" reports
  // to the debug-entity-state pub/sub topic (see CLAUDE.md).
  public static IHostApplicationBuilder AddVmsDebugState(this IHostApplicationBuilder builder)
  {
    builder.Services.AddSingleton(_ => new DaprClientBuilder().Build());
    builder.Services.AddSingleton<EntityStatePublisher>();
    return builder;
  }

  public static IHostApplicationBuilder AddVmsLogging(this IHostApplicationBuilder builder)
  {
    var appId  = Environment.GetEnvironmentVariable("APP_ID")
      ?? throw new InvalidOperationException(
        "Required environment variable 'APP_ID' is not set");
    var logDir = Environment.GetEnvironmentVariable("VMS_LOG_PATH");

    // A redirected console is written in the console's code page, which on Windows is the OEM one
    // (866 on a Russian system): TaVmsHost reads UTF-8, and Windows' localised error texts arrived
    // as mojibake. An interactive console is written through the Unicode API and needs nothing.
    if (Console.IsOutputRedirected)
      Console.OutputEncoding = Encoding.UTF8;

    var cfg = new LoggerConfiguration()
      .MinimumLevel.Debug()
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      // "Now listening on", "Application started": the one sign in a log that a service came up.
      .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
      .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
      // IHttpClientFactory's four lines per request (start, send, receive, end).
      .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
      .MinimumLevel.Override(OnvifLibCategory, LogEventLevel.Information)
      .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

    if (!string.IsNullOrWhiteSpace(logDir))
    {
      // Each service logs into its own subfolder: {logDir}/{appId}/{appId}.log
      // Size-based rotation keeps the filename clean ({appId}.log, {appId}_001.log).
      // A dedicated formatter guarantees valid JSON (an outputTemplate with
      // {Message:j} does NOT quote the message, producing broken JSON lines).
      var logFile = Path.Combine(logDir, appId, $"{appId}.log");
      cfg.WriteTo.File(new VmsJsonFormatter(), logFile,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        retainedFileCountLimit: 5,
        shared: true);
    }

    Log.Logger = cfg.CreateLogger();
    // Serilog is the only provider. The host's defaults (console, debug, event source, and the
    // Windows event log) would otherwise write everything a second time, past the overrides above:
    // two lines per gRPC request and the text of every SQL command at Information.
    builder.Logging.ClearProviders();
    builder.Logging.AddSerilog(dispose: true);
    return builder;
  }
}
