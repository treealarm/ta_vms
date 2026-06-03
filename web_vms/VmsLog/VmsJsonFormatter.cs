using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;

namespace VmsLog;

/// Emits one valid JSON object per line, matching the schema the C++ logger and
/// VmsLogger expect: {"timestamp","level","file","function","message"}.
/// Using a formatter (instead of an outputTemplate) guarantees the message is a
/// properly quoted/escaped JSON string — {Message:j} does NOT quote it.
internal sealed class VmsJsonFormatter : ITextFormatter
{
  public void Format(LogEvent logEvent, TextWriter output)
  {
    output.Write("{\"timestamp\":\"");
    output.Write(logEvent.Timestamp.UtcDateTime.ToString("O"));
    output.Write("\",\"level\":\"");
    output.Write(ShortLevel(logEvent.Level));
    output.Write("\",\"file\":");
    JsonValueFormatter.WriteQuotedJsonString(SourceContext(logEvent), output);
    output.Write(",\"function\":\"\",\"message\":");
    JsonValueFormatter.WriteQuotedJsonString(logEvent.RenderMessage(), output);
    if (logEvent.Exception is not null)
    {
      output.Write(",\"exception\":");
      JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
    }
    output.Write("}\n");
  }

  private static string SourceContext(LogEvent e) =>
    e.Properties.TryGetValue("SourceContext", out var v) && v is ScalarValue { Value: string s }
      ? s
      : string.Empty;

  private static string ShortLevel(LogEventLevel level) => level switch
  {
    LogEventLevel.Verbose     => "VRB",
    LogEventLevel.Debug       => "DBG",
    LogEventLevel.Information  => "INF",
    LogEventLevel.Warning      => "WRN",
    LogEventLevel.Error        => "ERR",
    LogEventLevel.Fatal        => "FTL",
    _                          => "INF",
  };
}
