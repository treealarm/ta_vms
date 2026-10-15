using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Dapr.Client;
using Microsoft.AspNetCore.Mvc;
using web_vms.Authorization;

namespace web_vms;

/// <summary>Map tiles for maps (and for aligning plans in real coordinates), fetched once from the configured provider and cached.</summary>
/// <remarks>
/// <para>
/// A proxy rather than the browser calling the provider itself, for the provider's sake: every pan
/// and zoom of every operator would otherwise be a request to it, and the OpenStreetMap tile policy
/// in particular requires caching, an identifying User-Agent and no more than two concurrent
/// downloads, and blocks heavy users. Square proxies its tiles for the same reason.
/// </para>
/// <para>
/// The cache is the Dapr state store, not a directory: this service runs as several replicas and
/// keeps no local files, and the store is shared by all of them, so a tile one replica fetched is a
/// hit for the rest. Rows expire by the store's own TTL.
/// </para>
/// </remarks>
[ApiController]
[Route("api/map/tiles")]
public class MapTilesController(
  DaprClient _dapr,
  IHttpClientFactory _http,
  ILogger<MapTilesController> _logger) : ControllerBase
{
  public const string HttpClientName = "map-tiles";

  /// Tile URL template with {z}, {x}, {y} and optionally {s}. Optional: unset means maps are
  /// drawn over a plain grid, which is also what a deployment with no internet access wants.
  public static readonly string? TileUrlTemplate =
    Environment.GetEnvironmentVariable("VMS_MAP_TILE_URL") is { Length: > 0 } url ? url : null;

  /// Names the provider in every cache key. The store outlives a change of VMS_MAP_TILE_URL by up to
  /// the TTL, and without it the old provider's tiles would keep answering under the new one.
  private static readonly string ProviderKey = TileUrlTemplate is null
    ? string.Empty
    : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TileUrlTemplate)))[..12];

  /// Most providers stop at 19; asking for more only produces misses against them.
  private const int MaxZoom = 19;

  /// Long enough that a map's tiles are fetched about once a month; the map of a building's
  /// surroundings does not change faster than that.
  private const int CacheTtlSeconds = 30 * 24 * 3600;

  /// The OpenStreetMap policy allows two parallel downloads per client; per process is the closest
  /// this service can honour without coordinating replicas.
  private static readonly SemaphoreSlim Downloads = new(2, 2);

  public static readonly string UserAgent =
    $"TreeAlarm-VMS/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"} (+https://treealarm.dev)";

  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private sealed record CachedTile(string ContentType, byte[] Data);

  [HttpGet("{z:int}/{x:int}/{y:int}")]
  [NoCameraAccess("Map tiles are public cartography, not a camera")]
  public async Task<IActionResult> Get(int z, int x, int y, CancellationToken ct)
  {
    if (TileUrlTemplate is null)
      return NotFound(new { error = "No map tile provider is configured (VMS_MAP_TILE_URL)" });

    var extent = 1 << Math.Clamp(z, 0, MaxZoom);
    if (z < 0 || z > MaxZoom || x < 0 || y < 0 || x >= extent || y >= extent)
      return BadRequest(new { error = "No such tile" });

    var key = $"tile:{ProviderKey}:{z}:{x}:{y}";
    var tile = await ReadCacheAsync(key, ct);
    if (tile is null)
    {
      tile = await DownloadAsync(z, x, y, ct);
      if (tile is null)
        return StatusCode(StatusCodes.Status502BadGateway, new { error = "The map tile provider did not answer" });
      // Kept even if the browser has given up by now: the download is already paid for.
      await WriteCacheAsync(key, tile, CancellationToken.None);
    }
    else
    {
      _logger.LogDebug("Tile cache hit for {Key}", key);
    }

    // The browser keeps it too, so panning back over the same area costs nothing at all.
    Response.Headers.CacheControl = "private, max-age=86400";
    return File(tile.Data, tile.ContentType);
  }

  private async Task<CachedTile?> ReadCacheAsync(string key, CancellationToken ct)
  {
    try
    {
      return await _dapr.GetStateAsync<CachedTile?>(StateStoreName, key, cancellationToken: ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // A store outage costs a download, not the map.
      _logger.LogWarning(ex, "Reading a cached map tile failed; fetching it from the provider");
      return null;
    }
  }

  private async Task WriteCacheAsync(string key, CachedTile tile, CancellationToken ct)
  {
    try
    {
      await _dapr.SaveStateAsync(StateStoreName, key, tile,
        metadata: new Dictionary<string, string> { ["ttlInSeconds"] = CacheTtlSeconds.ToString() },
        cancellationToken: ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      _logger.LogWarning(ex, "Caching a map tile failed; it will be fetched again next time");
    }
  }

  private async Task<CachedTile?> DownloadAsync(int z, int x, int y, CancellationToken ct)
  {
    var url = TileUrlTemplate!
      .Replace("{s}", "a")
      .Replace("{z}", z.ToString())
      .Replace("{x}", x.ToString())
      .Replace("{y}", y.ToString());

    // A browser that zoomed past this tile cancels it, and while it is still queued that drops it,
    // freeing the slot for a tile someone is looking at. Once it is downloading it is finished
    // regardless (the client's timeout still bounds it) and cached: abandoning it would spend a
    // provider request for nothing and send the same request again on the next zoom.
    await Downloads.WaitAsync(ct);
    try
    {
      using var response = await _http.CreateClient(HttpClientName).GetAsync(url, CancellationToken.None);
      if (!response.IsSuccessStatusCode)
      {
        _logger.LogWarning("Map tile provider answered {Status} for {Url}", (int)response.StatusCode, url);
        return null;
      }

      // A provider that is throttling, or a captive portal in between, can answer 200 with a page of
      // HTML. Cached, that page would be served as this tile for a month.
      var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/png";
      if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
      {
        _logger.LogWarning("Map tile provider answered {ContentType} instead of an image for {Url}", contentType, url);
        return null;
      }
      return new CachedTile(contentType, await response.Content.ReadAsByteArrayAsync(CancellationToken.None));
    }
    catch (HttpRequestException ex)
    {
      _logger.LogWarning(ex, "Map tile provider unreachable for {Url}", url);
      return null;
    }
    catch (TaskCanceledException)
    {
      // Nothing here cancels the download any more, so this is the client's timeout.
      _logger.LogWarning("Map tile provider timed out for {Url}", url);
      return null;
    }
    finally
    {
      Downloads.Release();
    }
  }
}
