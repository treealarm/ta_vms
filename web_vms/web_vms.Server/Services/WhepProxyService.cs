using System.Net.Http.Headers;

namespace web_vms;

public enum WhepProxyOutcome
{
  Success,
  InvalidPath,
  CameraNotFound,
  MediaMtxUnavailable,
  // The stream is not carrying video yet. Two ways of arriving at the same state: MediaMTX held
  // the offer waiting for a publisher until we gave up, or it answered 404 because the path is not
  // published at all. Neither is a fault — a stream stopped as idle only starts when a viewer asks
  // — and the honest answer to the browser is "not yet, ask again", which is a very different
  // thing to say than "the media server is broken".
  MediaMtxStreamNotReady,
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

  /// <summary>
  /// How long the gateway will hold a browser connection waiting for MediaMTX to answer an offer.
  /// Deliberately short. A browser allows six connections per origin, so an offer parked here is
  /// six-per-origin minus one for everything else on the page — and two of them starved the token
  /// refresh badly enough to end the session. Failing fast and letting the viewer retry gets the
  /// picture up at about the same moment, without spending a connection to wait for it.
  /// </summary>
  public static readonly TimeSpan MediaMtxOfferTimeout = TimeSpan.FromSeconds(4);

  // How long a session token stays usable — it is the token's own expiry (see WhepSessionToken),
  // nothing is stored here. Generous by design: this is not a security window (an expired token
  // could only close its own session, and MediaMTX times an abandoned WebRTC session out anyway)
  // but a ceiling on how long a viewer may run before it can no longer close what it opened. A
  // video wall left on the same layout for days must still tear its sessions down when it changes.
  private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

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
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      // The HttpClient timeout, not the caller giving up: MediaMTX is still waiting for a publisher.
      return new WhepOfferResult(WhepProxyOutcome.MediaMtxStreamNotReady);
    }

    using (mtxResponse)
    {
      string? token = null;
      if (mtxResponse.Headers.Location is { } location)
      {
        // What is sealed identifies the session, never a destination: the host to send the teardown
        // to is looked up from the configured MediaMTX servers by the sealed id when the time comes.
        var absoluteLocation = location.IsAbsoluteUri ? location : new Uri(targetUri, location);
        token = _tokens.Seal(
          new WhepSession(cameraId, server.Id, owner, absoluteLocation.PathAndQuery),
          DateTime.UtcNow + SessionTtl);
      }

      // The camera and its MediaMTX were both resolved from configuration above, so a 404 here is
      // never "unknown camera" — it is "this path is not published at the moment", the same state
      // the timeout above reports. Telling the browser 404 made every on-demand start look like a
      // failure in the console and gave the player nothing to act on.
      if (mtxResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
        return new WhepOfferResult(WhepProxyOutcome.MediaMtxStreamNotReady);

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
    if (!_tokens.TryOpen(token, out var session)
      || !string.Equals(session.Owner, owner, StringComparison.Ordinal))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    // The address comes from the configured MediaMTX servers, never from the token, so the request
    // can only ever reach a MediaMTX this deployment knows about. Looked up read-only on purpose:
    // GetOrAssignMediaMtxServer would *assign* a server as a side effect, and this endpoint is
    // reachable with nothing but a session token. The sealed path is then confined to the session's
    // own camera, checked after normalization so "/cam1/../something" cannot climb out.
    var servers = await _manager.ListMediaMtxServers(ct);
    var server = servers.FirstOrDefault(s => string.Equals(s.Id, session.ServerId, StringComparison.Ordinal));
    if (server is null || string.IsNullOrWhiteSpace(server.Host))
      return new WhepDeleteResult(WhepProxyOutcome.CameraNotFound);

    if (!Uri.TryCreate($"http://{server.Host}:{server.WebrtcPort}{session.SessionPath}", UriKind.Absolute, out var sessionUri)
      || !sessionUri.AbsolutePath.StartsWith($"/{session.CameraId}/", StringComparison.Ordinal))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    var client = _httpClientFactory.CreateClient(MediaMtxHttpClientName);
    using var request = new HttpRequestMessage(HttpMethod.Delete, sessionUri);

    try
    {
      using var mtxResponse = await client.SendAsync(request, ct);

      // Closing a session MediaMTX has already dropped — it times abandoned ones out on its own —
      // is not a failure: the session is gone, which is exactly what was asked for. Reporting the
      // 404 through made every ordinary teardown an error in the browser console.
      var status = mtxResponse.StatusCode == System.Net.HttpStatusCode.NotFound
        ? StatusCodes.Status204NoContent
        : (int)mtxResponse.StatusCode;
      return new WhepDeleteResult(WhepProxyOutcome.Success, status);
    }
    catch (HttpRequestException)
    {
      return new WhepDeleteResult(WhepProxyOutcome.MediaMtxUnavailable);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return new WhepDeleteResult(WhepProxyOutcome.MediaMtxStreamNotReady);
    }
  }

}
