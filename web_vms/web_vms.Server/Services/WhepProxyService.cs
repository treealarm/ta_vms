using System.Net.Http.Headers;
using System.Text;

namespace web_vms;

public enum WhepProxyOutcome
{
  Success,
  InvalidPath,
  CameraNotFound,
  MediaMtxUnavailable,
  InvalidToken,
  SsrfRejected,
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

  public async Task<WhepOfferResult> CreateOfferAsync(string path, Stream offerSdpBody, CancellationToken ct)
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

    var targetUri = new Uri($"http://{server.Host}:{server.WebrtcPort}/{path}/whep");

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
        token = EncodeSessionToken(absoluteLocation);
      }

      var body = await mtxResponse.Content.ReadAsByteArrayAsync(ct);
      var contentType = mtxResponse.Content.Headers.ContentType?.ToString() ?? "application/sdp";
      return new WhepOfferResult(WhepProxyOutcome.Success, (int)mtxResponse.StatusCode, contentType, body, token);
    }
  }

  public async Task<WhepDeleteResult> DeleteSessionAsync(string token, CancellationToken ct)
  {
    if (!TryDecodeSessionToken(token, out var targetUri))
      return new WhepDeleteResult(WhepProxyOutcome.InvalidToken);

    var servers = await _manager.ListMediaMtxServers(ct);
    var allowed = servers.Any(s =>
      string.Equals(s.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase) &&
      s.WebrtcPort == targetUri.Port);

    if (!allowed)
      return new WhepDeleteResult(WhepProxyOutcome.SsrfRejected);

    var client = _httpClientFactory.CreateClient(MediaMtxHttpClientName);
    using var request = new HttpRequestMessage(HttpMethod.Delete, targetUri);

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

  private static string EncodeSessionToken(Uri absoluteUri)
  {
    var bytes = Encoding.UTF8.GetBytes(absoluteUri.ToString());
    return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
  }

  private static bool TryDecodeSessionToken(string token, out Uri targetUri)
  {
    targetUri = null!;
    try
    {
      var base64 = token.Replace('-', '+').Replace('_', '/');
      base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
      var url = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
      return Uri.TryCreate(url, UriKind.Absolute, out targetUri!);
    }
    catch (FormatException)
    {
      return false;
    }
  }
}
