using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Cfg;
using web_vms.Authentication;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>
/// Every analytics source a camera has, in one answer. Read-only: the writes stay on the per-source
/// endpoints, each of which carries its own side effects, and a fan-out write would have nothing
/// honest to report when only some of its parts succeeded.
/// </summary>
/// <remarks>
/// Two services, one merge. VmsCfg answers "what is configured here" -- it owns every table
/// involved, so that half is a single call. VmsOnvif answers "what is the device actually doing",
/// which VmsCfg deliberately does not ask on a configuration read: that would put a cross-service
/// hop on a path that is otherwise its own database.
/// </remarks>
[ApiController]
[Route("api/analytics-sources")]
[Authorize(Policy = AuthPolicies.AdminOnly)]
public class AnalyticsSourcesController(
  CfgService.CfgServiceClient _cfgClient,
  OnvifRouter _onvifRouter) : ControllerBase
{
  private static readonly TimeSpan CfgCallTimeout = TimeSpan.FromSeconds(5);

  private static CallOptions CfgOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(CfgCallTimeout), cancellationToken: ct);

  /// <param name="cameraIds">
  /// Comma-separated filter; omitted means every camera. One endpoint rather than a list plus a
  /// by-id route because the only consumer batches: it asks for whatever set of cameras is on
  /// screen, which is usually one and occasionally all of them.
  /// </param>
  [HttpGet]
  public async Task<IActionResult> List([FromQuery] string? cameraIds, CancellationToken ct)
  {
    var request = new CameraAnalyticsSourcesRequest();
    request.CameraIds.AddRange(
      (cameraIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    var response = await _cfgClient.GetCameraAnalyticsSourcesAsync(request, CfgOptions(ct));

    // Only cameras we would consume boxes from are worth probing -- the probe is a per-node gRPC
    // call, and a camera we drop the boxes of has no button to light either way.
    var probe = response.Cameras.Where(c => c.OnvifMetadata.Accepted).Select(c => c.CameraId).ToList();
    var deviceEmits = probe.Count > 0
      ? await _onvifRouter.GetBboxCapableAsync(probe, ct)
      : [];

    return Ok(response.Cameras.Select(c => ToDto(c, deviceEmits)));
  }

  private static CameraAnalyticsSourcesDto ToDto(
    CameraAnalyticsSources src, IReadOnlyDictionary<string, bool> deviceEmits) => new()
  {
    CameraId = src.CameraId,
    Cv = src.Cv is null ? null : new CvSourceDto
    {
      Enabled          = src.Cv.Enabled,
      StreamName       = src.Cv.StreamName,
      Classes          = [.. src.Cv.Classes],
      MinConfidence    = src.Cv.MinConfidence,
      SampleFps        = src.Cv.SampleFps,
      TimeoutSeconds   = src.Cv.TimeoutSeconds,
      AttachDebugCrops = src.Cv.AttachDebugCrops,
    },
    OnvifMetadata = new OnvifMetadataSourceDto
    {
      Accepted = src.OnvifMetadata.Accepted,
      // Absent means "not probed yet", which is why this stays null rather than collapsing to false.
      DeviceEmits = deviceEmits.TryGetValue(src.CameraId, out var emits) ? emits : null,
    },
    OnvifEvents = new OnvifEventsSourceDto
    {
      Managed            = src.OnvifEvents.Managed,
      Subscribed         = src.OnvifEvents.Subscribed,
      PollTimeoutSeconds = src.OnvifEvents.PollTimeoutSeconds,
      MessageLimit       = src.OnvifEvents.MessageLimit,
      FilterCount        = src.OnvifEvents.FilterCount,
    },
  };
}
