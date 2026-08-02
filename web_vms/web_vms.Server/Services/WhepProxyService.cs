using System.Net.Http.Headers;

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
///
/// Holds no session state: the token handed to the browser is the session, sealed with AES-GCM
/// (see <see cref="WhepSessionToken"/>). That keeps DeleteSessionAsync targeting only URIs this
/// service itself constructed — the anti-SSRF guarantee a lookup table used to give — while
/// letting any replica close a session that any other replica opened.
/// </summary>
public sealed class WhepProxyService(
  ICameraManager _manager, IHttpClientFactory _httpClientFactory, WhepSessionToken _tokens)
{
  public const string MediaMtxHttpClientName = "mediamtx-whep";

  // How long a session token stays usable. It is the token's own expiry (see WhepSessionToken) —
  // nothing is stored here, so a session whose browser never came back to close it costs nothing
  // and needs no sweeping; MediaMTX times the abandoned WebRTC session out on its own.
  private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(1);

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
        token = _tokens.Seal(absoluteLocation, owner, DateTime.UtcNow + SessionTtl);
      }

      var body = await mtxResponse.Content.ReadAsByteArrayAsync(ct);
      var contentType = mtxResponse.Content.Headers.ContentType?.ToString() ?? "application/sdp";
      return new WhepOfferResult(WhepProxyOutcome.Success, (int)mtxResponse.StatusCode, contentType, body, token);
    }
  }

  public async Task<WhepDeleteResult> DeleteSessionAsync(string token, string owner, CancellationToken ct)
  {
    // A foreign token is answered exactly like an unopenable one, so tokens can't be probed and one
    // viewer can't tear down another's stream. Unlike the old in-memory table this is idempotent:
    // closing twice proxies twice and MediaMTX answers 404 the second time.
    if (!_tokens.TryOpen(token, out var sessionUri, out var sealedOwner)
      || !string.Equals(sealedOwner, owner, StringComparison.Ordinal))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    var client = _httpClientFactory.CreateClient(MediaMtxHttpClientName);
    using var request = new HttpRequestMessage(HttpMethod.Delete, sessionUri);

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

}
