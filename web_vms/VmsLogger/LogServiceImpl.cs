using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Logger;

namespace VmsLogger;

public sealed class LogServiceImpl : LogService.LogServiceBase
{
  private const uint DefaultLimit = 100;
  private const uint MaxLimit = 10000;
  private static readonly JsonParser Parser = JsonParser.Default;

  public override Task<ListLogSourcesResponse> ListLogSources(
    Empty request, ServerCallContext context)
  {
    var logPath = ResolveLogPath();
    var response = new ListLogSourcesResponse();

    if (Directory.Exists(logPath))
    {
      // Each service writes into its own subfolder named after its app_id.
      var appIds = Directory.EnumerateDirectories(logPath)
        .Select(Path.GetFileName)
        .Where(name => !string.IsNullOrEmpty(name))
        .Order();
      response.AppIds.AddRange(appIds!);
    }

    return Task.FromResult(response);
  }

  public override Task<LogResponse> GetLogs(LogRequest request, ServerCallContext context)
  {
    if (request is null)
    {
      throw new RpcException(new Status(StatusCode.InvalidArgument, "empty request"));
    }

    var limit = request.Limit == 0 ? DefaultLimit : request.Limit;
    if (limit > MaxLimit)
    {
      throw new RpcException(new Status(StatusCode.InvalidArgument, $"limit must be <= {MaxLimit}"));
    }

    var from = request.From ?? new Timestamp();
    var to = request.To;
    if (to is not null && CompareTimestamps(to, from) < 0)
    {
      throw new RpcException(new Status(StatusCode.InvalidArgument, "'to' must be greater than or equal to 'from'"));
    }

    var levelFilter = request.HasLevel && !string.IsNullOrWhiteSpace(request.Level)
      ? request.Level
      : null;
    var sourceFilter = request.HasSource && !string.IsNullOrWhiteSpace(request.Source)
      ? request.Source
      : null;

    var appIdFilter = request.HasAppId && !string.IsNullOrWhiteSpace(request.AppId)
      ? request.AppId
      : null;

    var response = new LogResponse();

    // Collect the newest `limit` matching entries, walking files newest-first.
    var collected = new List<LogEntry>();  // freshest-first
    foreach (var file in ResolveLogFiles(ResolveLogPath(), appIdFilter))
    {
      if (collected.Count >= limit)
      {
        response.HasMore = true;
        break;
      }
      ReadFileTail(file, from, to, levelFilter, sourceFilter, limit, collected, response);
    }

    // Return in chronological order (oldest-first); the client renders newest on top.
    collected.Reverse();
    response.Logs.AddRange(collected);
    return Task.FromResult(response);
  }

  // Appends the last (limit - collected.Count) matching entries of `file` to `collected`,
  // newest-first. Sets HasMore if the file held more matches than were taken.
  private static void ReadFileTail(
    string file,
    Timestamp from,
    Timestamp? to,
    string? levelFilter,
    string? sourceFilter,
    uint limit,
    List<LogEntry> collected,
    LogResponse response)
  {
    if (!File.Exists(file))
    {
      return;
    }

    var remaining = (int)limit - collected.Count;
    if (remaining <= 0)
    {
      response.HasMore = true;
      return;
    }

    // Keep only the last `remaining` matching entries (chronological within the file).
    var tail = new LinkedList<LogEntry>();
    var matchedInFile = 0L;
    try
    {
      foreach (var line in ReadLinesShared(file))
      {
        if (!TryParseLogEntry(line, out var entry))
          continue;
        if (!MatchesFilters(entry, from, to, levelFilter, sourceFilter))
          continue;

        matchedInFile++;
        tail.AddLast(entry);
        if (tail.Count > remaining)
          tail.RemoveFirst();
      }
    }
    catch (IOException)
    {
      // File may be locked/rotated mid-read by the writing service — skip it
      // rather than failing the whole request.
      return;
    }

    // Append newest-first.
    for (var node = tail.Last; node is not null; node = node.Previous)
      collected.Add(node.Value);

    if (matchedInFile > tail.Count)
      response.HasMore = true;
  }

  // Reads lines with FileShare.ReadWrite so reading does not fail while the owning
  // service (C++ spdlog / Serilog) keeps the active log file open for writing.
  private static IEnumerable<string> ReadLinesShared(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
      yield return line;
  }

  private static bool TryParseLogEntry(string line, out LogEntry entry)
  {
    try
    {
      entry = Parser.Parse<LogEntry>(line);
      return true;
    }
    catch
    {
      entry = new LogEntry();
      return false;
    }
  }

  private static bool MatchesFilters(
    LogEntry entry,
    Timestamp from,
    Timestamp? to,
    string? levelFilter,
    string? sourceFilter)
  {
    if (CompareTimestamps(entry.Timestamp ?? new Timestamp(), from) < 0)
    {
      return false;
    }

    if (to is not null && CompareTimestamps(entry.Timestamp ?? new Timestamp(), to) > 0)
    {
      return false;
    }

    if (levelFilter is not null)
    {
      // Threshold filter: selecting "warning" also includes error/critical.
      var minRank = LevelRank(levelFilter);
      if (minRank >= 0)
      {
        var entryRank = LevelRank(entry.Level);
        // Drop only entries whose level is recognized AND below the threshold;
        // unknown levels are kept so nothing is silently lost.
        if (entryRank >= 0 && entryRank < minRank)
          return false;
      }
      else if (!ContainsIgnoreCase(entry.Level, levelFilter))
      {
        return false;
      }
    }

    if (sourceFilter is not null)
    {
      var sourceMatch =
        ContainsIgnoreCase(entry.File, sourceFilter) ||
        ContainsIgnoreCase(entry.Function, sourceFilter) ||
        ContainsIgnoreCase(entry.Message, sourceFilter);
      if (!sourceMatch)
      {
        return false;
      }
    }

    return true;
  }

  private static IReadOnlyList<string> ResolveLogFiles(string baseDir, string? appId)
  {
    if (!Directory.Exists(baseDir))
      return [];

    // Filter to one service subfolder, or aggregate across all of them.
    var serviceDirs = appId is not null
      ? new[] { Path.Combine(baseDir, appId) }
      : Directory.EnumerateDirectories(baseDir).ToArray();

    var files = new List<string>();
    foreach (var dir in serviceDirs)
    {
      if (!Directory.Exists(dir))
        continue;
      files.AddRange(Directory.EnumerateFiles(dir, "*.log"));
    }

    // Newest file first — last write time is more reliable than guessing from the
    // rotation suffix (Serilog writes into the highest-numbered file).
    return files.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
  }

  // Maps level names to an ordinal severity, accepting both full words (C++ logger:
  // WARNING/ERROR) and Serilog's 3-letter form (WRN/ERR). Returns -1 if unrecognized.
  private static int LevelRank(string? level) =>
    string.IsNullOrWhiteSpace(level) ? -1 : level.Trim().ToLowerInvariant() switch
    {
      "trace" or "trc" or "vrb" or "verbose"        => 0,
      "debug" or "dbg"                              => 1,
      "info" or "inf" or "information"              => 2,
      "warn" or "warning" or "wrn"                  => 3,
      "error" or "err"                              => 4,
      "critical" or "crit" or "fatal" or "ftl"      => 5,
      _                                             => -1,
    };

  private static int CompareTimestamps(Timestamp lhs, Timestamp rhs)
  {
    var secondsComparison = lhs.Seconds.CompareTo(rhs.Seconds);
    return secondsComparison != 0
      ? secondsComparison
      : lhs.Nanos.CompareTo(rhs.Nanos);
  }

  private static bool ContainsIgnoreCase(string? haystack, string needle)
  {
    return haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false;
  }

  private static string ResolveLogPath() =>
    Environment.GetEnvironmentVariable("VMS_LOG_PATH")
      ?? throw new InvalidOperationException("Required environment variable 'VMS_LOG_PATH' is not set");
}
