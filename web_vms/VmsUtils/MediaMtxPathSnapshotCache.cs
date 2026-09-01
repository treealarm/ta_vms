using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VmsUtils;

public readonly record struct MediaMtxPathState(int ReadersCount, bool HasSource);

/// One snapshot of every path on a MediaMTX server, taken with a single /v3/paths/list.
///
/// The alternative — /v3/paths/get per path — makes the request rate on the control API grow with
/// the number of published streams and again with the number of media servers asking. Listing costs
/// one request per server per snapshot no matter how many paths are involved, and the short TTL
/// means several servers polling the same MediaMTX within the same moment share one fetch.
public sealed class MediaMtxPathSnapshotCache(
  IHttpClientFactory httpClientFactory,
  ILogger<MediaMtxPathSnapshotCache> logger)
{
  /// Well under the callers' poll interval, so a snapshot is never stale enough to change a
  /// start/stop decision, but long enough to collapse a burst of simultaneous polls into one fetch.
  private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(2);

  /// MediaMTX pages its list endpoints. Asking for a large page keeps a normal deployment to a
  /// single request while still following pageCount when the server caps it lower.
  private const int ItemsPerPage = 1000;

  private sealed record Snapshot(IReadOnlyDictionary<string, MediaMtxPathState> Paths, DateTime FetchedAt);
  private sealed record CacheKey(string ApiUrl, string ReadUser, string ReadPass);

  private sealed class Entry
  {
    public Snapshot? Snapshot;
    public readonly SemaphoreSlim Lock = new(1, 1);
  }

  private readonly HttpClient _http = httpClientFactory.CreateClient(UtilsServiceImpl.MediaMtxHttpClientName);
  private readonly ConcurrentDictionary<CacheKey, Entry> _entries = new();

  /// Returns null when the server's API could not be read — that is "no answer", not "no paths",
  /// and callers must not read it as "nobody is watching".
  public async Task<IReadOnlyDictionary<string, MediaMtxPathState>?> GetAsync(
    string apiUrl, string readUser, string readPass, CancellationToken ct)
  {
    var key = new CacheKey(apiUrl, readUser.Trim(), readPass);
    var entry = _entries.GetOrAdd(key, _ => new Entry());

    var current = entry.Snapshot;
    if (current is not null && DateTime.UtcNow - current.FetchedAt < Ttl)
      return current.Paths;

    await entry.Lock.WaitAsync(ct);
    try
    {
      // Another caller may have refreshed it while we waited.
      current = entry.Snapshot;
      if (current is not null && DateTime.UtcNow - current.FetchedAt < Ttl)
        return current.Paths;

      var paths = await FetchAsync(key, ct);
      if (paths is null)
        return null;

      entry.Snapshot = new Snapshot(paths, DateTime.UtcNow);
      return paths;
    }
    finally
    {
      entry.Lock.Release();
    }
  }

  private async Task<IReadOnlyDictionary<string, MediaMtxPathState>?> FetchAsync(
    CacheKey key, CancellationToken ct)
  {
    var result = new Dictionary<string, MediaMtxPathState>(StringComparer.Ordinal);

    try
    {
      var page = 0;
      while (true)
      {
        var url = $"{key.ApiUrl}/v3/paths/list?page={page}&itemsPerPage={ItemsPerPage}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (key.ReadUser.Length > 0)
        {
          var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{key.ReadUser}:{key.ReadPass}"));
          request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
          logger.LogWarning(
            "MediaMTX paths/list on '{ApiUrl}' returned {StatusCode}", key.ApiUrl, (int)response.StatusCode);
          return null;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
          foreach (var item in items.EnumerateArray())
          {
            if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
              continue;

            var readersCount = item.TryGetProperty("readers", out var readers)
                && readers.ValueKind == JsonValueKind.Array
              ? readers.GetArrayLength()
              : 0;
            var hasSource = item.TryGetProperty("source", out var source)
              && source.ValueKind != JsonValueKind.Null;

            result[name.GetString()!] = new MediaMtxPathState(readersCount, hasSource);
          }
        }

        var pageCount = root.TryGetProperty("pageCount", out var pc) && pc.ValueKind == JsonValueKind.Number
          ? pc.GetInt32()
          : 1;
        if (++page >= pageCount)
          break;
      }

      return result;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "MediaMTX paths/list on '{ApiUrl}' failed", key.ApiUrl);
      return null;
    }
  }
}
