using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace web_vms;

public enum WhepProxyOutcome
{
  Success,
  InvalidPath,
  CameraNotFound,
  MediaMtxUnavailable,
  InvalidToken,
}

public sealed record WhepOfferResult(
  WhepProxyOutcome Outcome,
  int StatusCode = 0,
  string? ContentType = null,
  byte[]? Body = null,
  string? SessionToken = null);

public sealed record WhepDeleteResult(WhepProxyOutcome Outcome, int StatusCode = 0);

/// <summary>
/// Proxies WHEP (WebRTC-HTTP Egress Protocol) signaling to the camera's assigned MediaMTX
/// server, so the browser never addresses MediaMTX directly. Only POST (create session) and
/// DELETE (end session) are proxied — current WHEP clients in this app use non-trickle ICE, so
/// no PATCH is needed. Shared by `WhepController` (authenticated) and `WhepTestController`
/// (anonymous, dev-only).
/// </summary>
public sealed class WhepProxyService(ICameraManager _manager, IHttpClientFactory _httpClientFactory)
{
  public const string MediaMtxHttpClientName = "mediamtx-whep";

  // Session token -> real MediaMTX session URL. The token handed to the browser is an opaque
  // random key into this table, never the URL itself — so DeleteSessionAsync only ever targets a
  // URI this service itself constructed in CreateOfferAsync, closing off SSRF-via-forged-token by
  // construction instead of having to re-validate a client-decodable URL against an allowlist.
  private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(1);
  private readonly ConcurrentDictionary<string, (Uri Uri, DateTime CreatedAt, string Owner)> _sessions = new();

  /// <param name="owner">
  /// Keycloak preferred_username of the caller, empty when authentication is disabled — the same
  /// identity idiom monitors use. Only this owner may later delete the session.
  /// </param>
  public async Task<WhepOfferResult> CreateOfferAsync(
    string path, Stream offerSdpBody, string owner, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(path))
      return new WhepOfferResult(WhepProxyOutcome.InvalidPath);

    var slashIndex = path.IndexOf('/');
    var cameraId = slashIndex < 0 ? path : path[..slashIndex];
    if (string.IsNullOrWhiteSpace(cameraId))
      return new WhepOfferResult(WhepProxyOutcome.InvalidPath);

    var server = await _manager.GetOrAssignMediaMtxServer(cameraId, ct);
    if (server is null || string.IsNullOrWhiteSpace(server.Host))
      return new WhepOfferResult(WhepProxyOutcome.CameraNotFound);

    Uri targetUri;
    try
    {
      targetUri = new Uri($"http://{server.Host}:{server.WebrtcPort}/{Uri.EscapeDataString(path).Replace("%2F", "/")}/whep");
    }
    catch (UriFormatException)
    {
      return new WhepOfferResult(WhepProxyOutcome.InvalidPath);
    }

    using var offerContent = new StreamContent(offerSdpBody);
    offerContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/sdp");

    var client = _httpClientFactory.CreateClient(MediaMtxHttpClientName);

    HttpResponseMessage mtxResponse;
    try
    {
      mtxResponse = await client.PostAsync(targetUri, offerContent, ct);
    }
    catch (HttpRequestException)
    {
      return new WhepOfferResult(WhepProxyOutcome.MediaMtxUnavailable);
    }

    using (mtxResponse)
    {
      string? token = null;
      if (mtxResponse.Headers.Location is { } location)
      {
        var absoluteLocation = location.IsAbsoluteUri ? location : new Uri(targetUri, location);
        token = CreateSessionToken(absoluteLocation, owner);
      }

      var body = await mtxResponse.Content.ReadAsByteArrayAsync(ct);
      var contentType = mtxResponse.Content.Headers.ContentType?.ToString() ?? "application/sdp";
      return new WhepOfferResult(WhepProxyOutcome.Success, (int)mtxResponse.StatusCode, contentType, body, token);
    }
  }

  public async Task<WhepDeleteResult> DeleteSessionAsync(string token, string owner, CancellationToken ct)
  {
    // Check ownership before removing, and answer a foreign token exactly like an unknown one, so
    // tokens can't be probed for existence and one viewer can't tear down another's stream.
    if (!_sessions.TryGetValue(token, out var session)
      || !string.Equals(session.Owner, owner, StringComparison.Ordinal)
      || !_sessions.TryRemove(token, out session))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    var client = _httpClientFactory.CreateClient(MediaMtxHttpClientName);
    using var request = new HttpRequestMessage(HttpMethod.Delete, session.Uri);

    try
    {
      using var mtxResponse = await client.SendAsync(request, ct);
      return new WhepDeleteResult(WhepProxyOutcome.Success, (int)mtxResponse.StatusCode);
    }
    catch (HttpRequestException)
    {
      return new WhepDeleteResult(WhepProxyOutcome.MediaMtxUnavailable);
    }
  }

  private string CreateSessionToken(Uri sessionUri, string owner)
  {
    PruneExpiredSessions();

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    _sessions[token] = (sessionUri, DateTime.UtcNow, owner);
    return token;
  }

  // Best-effort sweep for sessions whose browser-side cleanup never arrived (e.g. tab closed
  // mid-stream) — same "no delivery guarantees, periodic sweep" spirit as the debug-entity-state
  // registry, just in-process and unrelated to it.
  private void PruneExpiredSessions()
  {
    var cutoff = DateTime.UtcNow - SessionTtl;
    foreach (var (key, value) in _sessions)
    {
      if (value.CreatedAt < cutoff)
        _sessions.TryRemove(key, out _);
    }
  }
}
