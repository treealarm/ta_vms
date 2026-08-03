using Microsoft.AspNetCore.Mvc;

namespace web_vms;

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
    _ => controller.StatusCode(result.StatusCode),
  };
}
