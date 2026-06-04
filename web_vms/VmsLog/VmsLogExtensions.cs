using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

namespace VmsLog;

public static class VmsLogExtensions
{
  public static IHostApplicationBuilder AddVmsLogging(this IHostApplicationBuilder builder)
  {
    var appId  = Environment.GetEnvironmentVariable("APP_ID")
      ?? throw new InvalidOperationException(
        "Required environment variable 'APP_ID' is not set");
    var logDir = Environment.GetEnvironmentVariable("VMS_LOG_PATH");

    var cfg = new LoggerConfiguration()
      .MinimumLevel.Debug()
      .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
      .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
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
    builder.Logging.AddSerilog(dispose: true);
    return builder;
  }
}
