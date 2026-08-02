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
/// (see <see cref="WhepSessionToken"/>), so any replica can close a session that any other replica
/// opened. Both directions resolve the MediaMTX address from the camera's configuration, so a
/// request can only ever reach a configured MediaMTX no matter what the client sends — the
/// anti-SSRF guarantee the old lookup table gave by construction, kept by construction.
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
        // Only the path is sealed. The host is re-derived from the camera's configuration when the
        // session is closed, so the token cannot carry a destination anywhere.
        var absoluteLocation = location.IsAbsoluteUri ? location : new Uri(targetUri, location);
        token = _tokens.Seal(cameraId, absoluteLocation.PathAndQuery, owner, DateTime.UtcNow + SessionTtl);
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
    if (!_tokens.TryOpen(token, out var cameraId, out var sessionPath, out var sealedOwner)
      || !string.Equals(sealedOwner, owner, StringComparison.Ordinal))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    // Where to send the DELETE comes from the camera's configuration, never from the token — the
    // request the client controls can only ever reach the MediaMTX this camera is assigned to. The
    // sealed path is confined to that camera's own namespace on it, checked after normalization so
    // "/cam1/../something" cannot climb out.
    var server = await _manager.GetOrAssignMediaMtxServer(cameraId, ct);
    if (server is null || string.IsNullOrWhiteSpace(server.Host))
      return new WhepDeleteResult(WhepProxyOutcome.CameraNotFound);

    if (!Uri.TryCreate($"http://{server.Host}:{server.WebrtcPort}{sessionPath}", UriKind.Absolute, out var sessionUri)
      || !sessionUri.AbsolutePath.StartsWith($"/{cameraId}/", StringComparison.Ordinal))
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
