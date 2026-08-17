using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Vms.Fs;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms;

/// <summary>
/// Analytics geometry for a slice of the archive, so an archive player can draw the same boxes the
/// live overlay draws.
/// </summary>
/// <remarks>
/// On ArchController rather than FSController deliberately: the parameters and the access check are
/// the ones hls/preview.m3u8 already uses, and "may this user see this camera's archive" should be
/// decided in the same file as the playlist these boxes are drawn over.
/// </remarks>
public partial class ArchController
{
  /// <summary>
  /// Longest window one call may ask for. The client fetches in aligned one-minute buckets, so this
  /// is slack, not a limit it works against — it exists so a hand-built URL cannot turn the fan-out
  /// into a way to pull a day of geometry out of every FS instance at once.
  /// </summary>
  private static readonly TimeSpan MaxMetadataWindow = TimeSpan.FromMinutes(5);

  [CameraAccess(CameraAction.ViewArchive)]
  [HttpGet("metadata")]
  public async Task<IActionResult> GetArchiveMetadata(
    [FromQuery] string cameraId,
    [FromQuery] string from,
    [FromQuery] string to,
    [FromQuery] string? streamName = null,
    CancellationToken ct = default)
  {
    if (!TryParseWindow(cameraId, from, to, out var camId, out var fromDt, out var toDt, out var error))
      return BadRequest(new { error });

    var request = new GetMetadataSamplesRequest
    {
      CameraId = camId,
      FromTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(fromDt),
      ToTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(toDt),
    };
    if (!string.IsNullOrWhiteSpace(streamName))
      request.StreamName = streamName.Trim();

    // Fan-out rather than "ask the camera's current server": a camera reassigned to another node
    // leaves its old clips — and their geometry — behind on the old FS, exactly as event-clips and
    // event-playlists already have to account for.
    var results = await registry.FanOutFsAsync(
      (c, o) => c.GetMetadataSamplesAsync(request, o).ResponseAsync, ct);

    var bySource = new Dictionary<string, List<ArchiveMetadataFrameDto>>();
    foreach (var result in results)
    {
      foreach (var source in result.Value.Sources)
      {
        if (!bySource.TryGetValue(source.Source, out var frames))
          bySource[source.Source] = frames = [];
        foreach (var frame in source.Frames)
        {
          frames.Add(new ArchiveMetadataFrameDto(
            frame.Ts.ToDateTime(),
            frame.Boxes.Select(b => new ArchiveMetadataBoxDto(
              b.TrackId, b.Kind, b.Confidence, b.X, b.Y, b.Width, b.Height,
              b.HasLabel ? b.Label : null,
              b.HasPlateText ? b.PlateText : null)).ToList()));
        }
      }
    }

    return Ok(new ArchiveMetadataResponseDto(
      bySource
        .Select(kv => new ArchiveMetadataSourceDto(kv.Key, kv.Value.OrderBy(f => f.Ts).ToList()))
        .ToList()));
  }

  [CameraAccess(CameraAction.ViewArchive)]
  [HttpGet("metadata/coverage")]
  public async Task<IActionResult> GetArchiveMetadataCoverage(
    [FromQuery] string cameraId,
    [FromQuery] string from,
    [FromQuery] string to,
    [FromQuery] string? streamName = null,
    CancellationToken ct = default)
  {
    // The coverage window follows the playlist's, which is far wider than the sample window — the
    // cap does not apply here, and the query is two index probes regardless of span.
    if (!TryParseWindow(cameraId, from, to, out var camId, out var fromDt, out var toDt, out var error,
          enforceMaxWindow: false))
      return BadRequest(new { error });

    var request = new GetMetadataCoverageRequest
    {
      CameraId = camId,
      FromTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(fromDt),
      ToTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(toDt),
    };
    if (!string.IsNullOrWhiteSpace(streamName))
      request.StreamName = streamName.Trim();

    var results = await registry.FanOutFsAsync(
      (c, o) => c.GetMetadataCoverageAsync(request, o).ResponseAsync, ct);

    return Ok(new ArchiveMetadataCoverageDto(
      results.Any(r => r.Value.HasCv),
      results.Any(r => r.Value.HasOnvif)));
  }

  private static bool TryParseWindow(
    string? cameraId, string? from, string? to,
    out string camId, out DateTime fromDt, out DateTime toDt, out string? error,
    bool enforceMaxWindow = true)
  {
    camId = cameraId?.Trim() ?? "";
    fromDt = default;
    toDt = default;
    error = null;

    if (camId.Length == 0 || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
    {
      error = "cameraId, from, and to are required";
      return false;
    }

    if (!DateTime.TryParse(from.Trim(), null, DateTimeStyles.RoundtripKind, out var parsedFrom)
        || !DateTime.TryParse(to.Trim(), null, DateTimeStyles.RoundtripKind, out var parsedTo))
    {
      error = "from and to must be parseable date/time";
      return false;
    }

    fromDt = ToUtc(parsedFrom);
    toDt = ToUtc(parsedTo);
    if (fromDt >= toDt)
    {
      error = "from must be before to";
      return false;
    }

    if (enforceMaxWindow && toDt - fromDt > MaxMetadataWindow)
    {
      error = $"window must not exceed {MaxMetadataWindow.TotalMinutes:0} minutes";
      return false;
    }

    return true;
  }
}
