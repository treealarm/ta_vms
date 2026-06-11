using System.Text;
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
      if (ReadFileTail(file, from, to, levelFilter, sourceFilter, limit, collected, response))
        break;
    }

    // Return in chronological order (oldest-first); the client renders newest on top.
    collected.Reverse();
    response.Logs.AddRange(collected);
    return Task.FromResult(response);
  }

  // Appends matching entries of `file` to `collected`, newest-first, scanning backward from
  // EOF. Returns true if the caller should stop iterating files entirely (the overall `limit`
  // has been reached); false to continue with the next (older) file.
  private static bool ReadFileTail(
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
      return false;
    }

    var remaining = (int)limit - collected.Count;
    if (remaining <= 0)
    {
      response.HasMore = true;
      return true;
    }

    try
    {
      foreach (var line in ReadLinesReversedShared(file))
      {
        if (!TryParseLogEntry(line, out var entry))
          continue;

        if (IsAfterTo(entry, to))
          continue; // newer than `to`, skip but keep scanning backward

        if (IsBeforeFrom(entry, from))
          return false; // this file is exhausted for [from, to]; move to the next file

        if (!MatchesLevelAndSource(entry, levelFilter, sourceFilter))
          continue;

        collected.Add(entry); // newest-first, same order the caller expects
        remaining--;
        if (remaining == 0)
        {
          response.HasMore = true;
          return true; // limit reached: stop the whole multi-file loop
        }
      }
    }
    catch (IOException)
    {
      // File may be locked/rotated mid-read by the writing service — skip it
      // rather than failing the whole request.
      return false;
    }

    return false; // reached BOF without hitting `from` or `limit`; continue to next file
  }

  private const int ReverseReadBufferSize = 64 * 1024;

  // Reads lines from EOF to BOF, with FileShare.ReadWrite so reading does not fail while the
  // owning service (C++ spdlog / Serilog) keeps the active log file open for writing.
  private static IEnumerable<string> ReadLinesReversedShared(string path)
  {
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    var position = stream.Length;
    var leftover = Array.Empty<byte>();
    var buffer = new byte[ReverseReadBufferSize];

    while (position > 0)
    {
      var chunkSize = (int)Math.Min(ReverseReadBufferSize, position);
      position -= chunkSize;
      stream.Seek(position, SeekOrigin.Begin);
      stream.ReadExactly(buffer, 0, chunkSize);

      // `combined` = this chunk followed by the partial line carried over from the chunk
      // read in the previous iteration (which is later in the file).
      var combined = new byte[chunkSize + leftover.Length];
      Buffer.BlockCopy(buffer, 0, combined, 0, chunkSize);
      Buffer.BlockCopy(leftover, 0, combined, chunkSize, leftover.Length);

      // Splitting raw bytes on 0x0A ('\n') before UTF-8 decoding is safe: 0x0A never occurs
      // as a continuation or lead byte of a multi-byte UTF-8 sequence (those are all >= 0x80),
      // so each segment between newlines is independently valid UTF-8.
      var segmentEnd = combined.Length;
      for (var i = combined.Length - 1; i >= 0; i--)
      {
        if (combined[i] != (byte)'\n')
          continue;

        yield return Encoding.UTF8.GetString(combined, i + 1, segmentEnd - (i + 1));
        segmentEnd = i;
      }

      leftover = combined[..segmentEnd];
    }

    if (leftover.Length > 0)
      yield return Encoding.UTF8.GetString(leftover);
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

  // entry.Timestamp < from -> caller should stop scanning this file (everything earlier
  // in the file is older too).
  private static bool IsBeforeFrom(LogEntry entry, Timestamp from) =>
    CompareTimestamps(entry.Timestamp ?? new Timestamp(), from) < 0;

  // entry.Timestamp > to -> caller should skip this entry but keep scanning backward
  // (older entries further down may still satisfy <= to).
  private static bool IsAfterTo(LogEntry entry, Timestamp? to) =>
    to is not null && CompareTimestamps(entry.Timestamp ?? new Timestamp(), to) > 0;

  private static bool MatchesLevelAndSource(
    LogEntry entry,
    string? levelFilter,
    string? sourceFilter)
  {
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
