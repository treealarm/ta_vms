using Microsoft.AspNetCore.Mvc;

namespace web_srv;

internal static class WhepResultMapper
{
  public static async Task<IActionResult> WriteTo(
    this WhepOfferResult result, ControllerBase controller, string sessionRouteBase, CancellationToken ct)
  {
    switch (result.Outcome)
    {
      case WhepProxyOutcome.InvalidPath:
        return controller.BadRequest("path must not be empty");
      case WhepProxyOutcome.CameraNotFound:
        return controller.NotFound("No MediaMTX server assigned to this camera");
      case WhepProxyOutcome.MediaMtxUnavailable:
        return controller.StatusCode(StatusCodes.Status502BadGateway, "MediaMTX is unavailable");
      case WhepProxyOutcome.MediaMtxStreamNotReady:
        // The stream is on its way up (EnsurePushAsync has already asked for it) — the viewer just
        // has to ask again. Retry-After is advisory here; the player has its own backoff.
        controller.Response.Headers.RetryAfter = "1";
        return controller.StatusCode(StatusCodes.Status503ServiceUnavailable, "stream is still starting, retry");
    }

    if (result.SessionToken is { } token)
      controller.Response.Headers.Location = $"{sessionRouteBase}{token}";

    controller.Response.StatusCode = result.StatusCode;
    controller.Response.ContentType = result.ContentType;
    await controller.Response.Body.WriteAsync(result.Body!, ct);
    return new EmptyResult();
  }

  public static IActionResult ToActionResult(this WhepDeleteResult result, ControllerBase controller) => result.Outcome switch
  {
    WhepProxyOutcome.InvalidToken => controller.BadRequest("invalid session token"),
    // The camera the token names has lost its MediaMTX assignment since the session started, so
    // there is nowhere to send the teardown — the session is MediaMTX's to time out now.
    WhepProxyOutcome.CameraNotFound => controller.NotFound("No MediaMTX server assigned to this camera"),
    WhepProxyOutcome.MediaMtxUnavailable => controller.StatusCode(StatusCodes.Status502BadGateway, "MediaMTX is unavailable"),
    WhepProxyOutcome.MediaMtxStreamNotReady => controller.StatusCode(StatusCodes.Status504GatewayTimeout, "MediaMTX did not answer"),
    _ => controller.StatusCode(result.StatusCode),
  };
}
