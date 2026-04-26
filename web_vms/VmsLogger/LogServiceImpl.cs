using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Logger;

namespace VmsLogger;

public sealed partial class LogServiceImpl : LogService.LogServiceBase
{
  private const uint DefaultLimit = 100;
  private const uint MaxLimit = 10000;
  private static readonly JsonParser Parser = JsonParser.Default;

  [GeneratedRegex(@"^service(?:\.log\.(\d+)|\.(\d+)\.log)$")]
  private static partial Regex RotationPattern();

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

    var response = new LogResponse();
    foreach (var file in ResolveLogFiles(ResolveStorageBasePath()))
    {
      ReadLogFile(file, from, to, levelFilter, sourceFilter, limit, response);
      if (response.HasMore)
      {
        break;
      }
    }

    return Task.FromResult(response);
  }

  private static void ReadLogFile(
    string file,
    Timestamp from,
    Timestamp? to,
    string? levelFilter,
    string? sourceFilter,
    uint limit,
    LogResponse response)
  {
    if (!File.Exists(file))
    {
      return;
    }

    foreach (var line in File.ReadLines(file))
    {
      if (!TryParseLogEntry(line, out var entry))
      {
        continue;
      }

      if (!MatchesFilters(entry, from, to, levelFilter, sourceFilter))
      {
        continue;
      }

      if (response.Logs.Count >= limit)
      {
        response.HasMore = true;
        return;
      }

      response.Logs.Add(entry);
    }
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

    if (levelFilter is not null && !ContainsIgnoreCase(entry.Level, levelFilter))
    {
      return false;
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

  private static IReadOnlyList<string> ResolveLogFiles(string baseDir)
  {
    var indexedFiles = new List<(int Index, string File)>();
    var activeLog = Path.Combine(baseDir, "service.log");
    if (File.Exists(activeLog))
    {
      indexedFiles.Add((0, activeLog));
    }

    if (Directory.Exists(baseDir))
    {
      foreach (var file in Directory.EnumerateFiles(baseDir))
      {
        var filename = Path.GetFileName(file);
        var match = RotationPattern().Match(filename);
        if (!match.Success)
        {
          continue;
        }

        var indexText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (int.TryParse(indexText, out var index))
        {
          indexedFiles.Add((index, file));
        }
      }
    }

    return indexedFiles
      .OrderByDescending(item => item.Index)
      .Select(item => item.File)
      .ToList();
  }

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

  private static string ResolveStorageBasePath()
  {
    var value = Environment.GetEnvironmentVariable("VMS_STORAGE_BASE_PATH");
    return string.IsNullOrWhiteSpace(value) ? "records" : value;
  }
}
