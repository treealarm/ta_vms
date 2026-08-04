using Dapr.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vms.Control;
using Vms.Fs;
using Vms.Onvif;
using web_vms.Authorization;
using web_vms.Types.Dto;

namespace web_vms.Server.Controllers;

/// <summary>
/// The camera's own archive (SD card) over ONVIF Profile G: what it holds, importing a range into
/// this VMS, and managing the camera's on-board recording and storage.
/// </summary>
/// <remarks>
/// Reads answer with a <c>message</c> instead of an error status when a camera simply cannot do
/// Profile G, so the UI can say "unsupported" rather than "something broke". Deletions and storage
/// formatting act on the device and cannot be undone — they require <see cref="CameraAction.Control"/>
/// and are logged.
/// </remarks>
[ApiController]
[Route("api/edge-archive")]
[Authorize]
public sealed class EdgeArchiveController(
  OnvifService.OnvifServiceClient onvifClient,
  ICameraManager manager,
  ServerPairRegistry registry,
  ICameraAccessService access,
  EdgeExportToken exportToken,
  ILogger<EdgeArchiveController> logger) : ControllerBase
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
  // A search walks several SOAP round trips on the device, each with its own internal timeout.
  private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

  // One export file per this many seconds. An hour of 2688x1520 is roughly 2 GB in a single piece,
  // which is awkward to download and cannot be previewed without pulling all of it.
  private static readonly uint ExportChunkSeconds =
    uint.TryParse(Environment.GetEnvironmentVariable("VMS_EDGE_EXPORT_CHUNK_SECONDS"), out var chunk) && chunk > 0
      ? chunk
      : throw new InvalidOperationException(
          "Required environment variable 'VMS_EDGE_EXPORT_CHUNK_SECONDS' is not set or is not a positive integer");

  private static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(Timeout), cancellationToken: ct);

  private static CallOptions SearchOptions(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.Add(SearchTimeout), cancellationToken: ct);

  // ── What the camera holds ────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/capabilities")]
  [CameraAccess(CameraAction.ViewArchive)]
  public async Task<IActionResult> GetCapabilities(string cameraId, CancellationToken ct)
  {
    var r = await onvifClient.GetEdgeCapabilitiesAsync(new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(new EdgeCapabilitiesDto
    {
      Recording = r.Recording,
      Search = r.Search,
      Replay = r.Replay,
      Supported = r.Search && r.Replay,
      DynamicRecordings = r.DynamicRecordings,
      DynamicTracks = r.DynamicTracks,
      StorageConfiguration = r.StorageConfiguration,
      AuxiliaryCommands = r.AuxiliaryCommands,
      MaxRecordings = r.MaxRecordings,
      SupportedAuxiliaryCommands = r.SupportedAuxiliaryCommands.ToList(),
      Message = r.Message,
    });
  }

  [HttpGet("cameras/{cameraId}/summary")]
  [CameraAccess(CameraAction.ViewArchive)]
  public async Task<IActionResult> GetSummary(string cameraId, CancellationToken ct)
  {
    var r = await onvifClient.GetEdgeRecordingSummaryAsync(new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(new EdgeSummaryDto
    {
      DataFrom = r.DataFrom,
      DataUntil = r.DataUntil,
      NumberRecordings = r.NumberRecordings,
      Supported = r.Supported,
      Message = r.Message,
      ClockOffsetMs = r.HasClockOffsetMs ? r.ClockOffsetMs : null,
    });
  }

  [HttpGet("cameras/{cameraId}/recordings")]
  [CameraAccess(CameraAction.ViewArchive)]
  public async Task<IActionResult> ListRecordings(string cameraId, CancellationToken ct)
  {
    var r = await onvifClient.ListEdgeRecordingsAsync(new CameraRequest { SourceId = cameraId }, SearchOptions(ct));
    return Ok(new EdgeRecordingsDto
    {
      Message = r.Message,
      ClockOffsetMs = r.HasClockOffsetMs ? r.ClockOffsetMs : null,
      Recordings = r.Recordings.Select(rec => new EdgeRecordingDto
      {
        RecordingToken = rec.RecordingToken,
        SourceName = rec.SourceName,
        SourceDescription = rec.SourceDescription,
        EarliestRecording = rec.EarliestRecording,
        LatestRecording = rec.LatestRecording,
        Content = rec.Content,
        RecordingStatus = rec.RecordingStatus,
        Tracks = rec.Tracks.Select(t => new EdgeTrackDto
        {
          TrackToken = t.TrackToken,
          TrackType = t.TrackType,
          Description = t.Description,
          DataFrom = t.DataFrom,
          DataTo = t.DataTo,
        }).ToList(),
      }).ToList(),
    });
  }

  [HttpGet("cameras/{cameraId}/intervals")]
  [CameraAccess(CameraAction.ViewArchive)]
  public async Task<IActionResult> ListIntervals(
    string cameraId,
    [FromQuery] string? from,
    [FromQuery] string? to,
    [FromQuery] string? recordingToken,
    [FromQuery] int maxResults,
    CancellationToken ct)
  {
    var r = await onvifClient.ListEdgeRecordingIntervalsAsync(new EdgeIntervalsRequest
    {
      SourceId = cameraId,
      StartTime = from ?? string.Empty,
      EndTime = to ?? string.Empty,
      RecordingToken = recordingToken ?? string.Empty,
      MaxResults = (uint)Math.Max(0, maxResults),
    }, SearchOptions(ct));

    return Ok(new EdgeIntervalsDto
    {
      Searched = r.Searched,
      Message = r.Message,
      ClockOffsetMs = r.HasClockOffsetMs ? r.ClockOffsetMs : null,
      Intervals = r.Intervals.Select(i => new EdgeIntervalDto
      {
        RecordingToken = i.RecordingToken,
        TrackToken = i.TrackToken,
        StartTime = i.StartTime,
        EndTime = i.EndTime,
      }).ToList(),
    });
  }

  // ── On-camera recording control ──────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/recording-jobs")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> ListRecordingJobs(string cameraId, CancellationToken ct)
  {
    var r = await onvifClient.ListEdgeRecordingJobsAsync(new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(new EdgeRecordingJobsDto
    {
      Message = r.Message,
      Jobs = r.Jobs.Select(j => new EdgeRecordingJobDto
      {
        JobToken = j.JobToken,
        RecordingToken = j.RecordingToken,
        Mode = j.Mode,
        SourceToken = j.SourceToken,
      }).ToList(),
      Configurations = r.Configurations.Select(c => new EdgeRecordingConfigurationDto
      {
        RecordingToken = c.RecordingToken,
        SourceId = c.SourceId,
        SourceName = c.SourceName,
        SourceLocation = c.SourceLocation,
        SourceDescription = c.SourceDescription,
        SourceAddress = c.SourceAddress,
        Content = c.Content,
        MaximumRetentionTime = c.MaximumRetentionTime,
      }).ToList(),
    });
  }

  [HttpPut("cameras/{cameraId}/recording-jobs/{jobToken}/mode")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> SetRecordingJobMode(
    string cameraId, string jobToken, [FromBody] EdgeJobModeRequestDto body, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(body?.Mode))
      return BadRequest(new { message = "mode is required (Active or Idle)" });

    logger.LogInformation("Setting camera {CameraId} recording job {JobToken} mode to {Mode}", cameraId, jobToken, body.Mode);
    var r = await onvifClient.SetEdgeRecordingJobModeAsync(new EdgeSetJobModeRequest
    {
      SourceId = cameraId, JobToken = jobToken, Mode = body.Mode,
    }, Options(ct));
    return FromMutation(r);
  }

  [HttpPut("cameras/{cameraId}/recordings/{recordingToken}/configuration")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> SetRecordingConfiguration(
    string cameraId, string recordingToken, [FromBody] EdgeRecordingConfigRequestDto body, CancellationToken ct)
  {
    logger.LogInformation("Updating camera {CameraId} recording {Token} configuration", cameraId, recordingToken);
    var r = await onvifClient.SetEdgeRecordingConfigurationAsync(new EdgeSetRecordingConfigurationRequest
    {
      SourceId = cameraId,
      RecordingToken = recordingToken,
      Content = body?.Content ?? string.Empty,
      MaximumRetentionTime = body?.MaximumRetentionTime ?? string.Empty,
    }, Options(ct));
    return FromMutation(r);
  }

  [HttpDelete("cameras/{cameraId}/recordings/{recordingToken}")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> DeleteRecording(string cameraId, string recordingToken, CancellationToken ct)
  {
    logger.LogWarning("Deleting recording {Token} on camera {CameraId}", recordingToken, cameraId);
    var r = await onvifClient.DeleteEdgeRecordingAsync(new EdgeRecordingTokenRequest
    {
      SourceId = cameraId, RecordingToken = recordingToken,
    }, Options(ct));
    return FromMutation(r);
  }

  [HttpDelete("cameras/{cameraId}/recordings/{recordingToken}/tracks/{trackToken}")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> DeleteTrack(string cameraId, string recordingToken, string trackToken, CancellationToken ct)
  {
    logger.LogWarning("Deleting track {Track} of recording {Token} on camera {CameraId}", trackToken, recordingToken, cameraId);
    var r = await onvifClient.DeleteEdgeTrackAsync(new EdgeTrackRequest
    {
      SourceId = cameraId, RecordingToken = recordingToken, TrackToken = trackToken,
    }, Options(ct));
    return FromMutation(r);
  }

  [HttpDelete("cameras/{cameraId}/recording-jobs/{jobToken}")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> DeleteRecordingJob(string cameraId, string jobToken, CancellationToken ct)
  {
    logger.LogWarning("Deleting recording job {JobToken} on camera {CameraId}", jobToken, cameraId);
    var r = await onvifClient.DeleteEdgeRecordingJobAsync(new EdgeJobRequest
    {
      SourceId = cameraId, JobToken = jobToken,
    }, Options(ct));
    return FromMutation(r);
  }

  // ── Camera-side storage ──────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/storage")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> ListStorage(string cameraId, CancellationToken ct)
  {
    var r = await onvifClient.ListEdgeStorageAsync(new CameraRequest { SourceId = cameraId }, Options(ct));
    return Ok(new EdgeStorageDto
    {
      Supported = r.Supported,
      FormatSupported = r.FormatSupported,
      FormatCommand = r.FormatCommand,
      Message = r.Message,
      Configurations = r.Configurations.Select(c => new EdgeStorageConfigurationDto
      {
        Token = c.Token,
        Type = c.Type,
        LocalPath = c.LocalPath,
        StorageUri = c.StorageUri,
        User = c.User,
      }).ToList(),
    });
  }

  [HttpPost("cameras/{cameraId}/storage/format")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> FormatStorage(
    string cameraId, [FromBody] EdgeFormatStorageRequestDto? body, CancellationToken ct)
  {
    logger.LogWarning("Formatting camera-side storage on {CameraId} (command '{Command}')",
      cameraId, body?.Command ?? "<device default>");
    var r = await onvifClient.FormatEdgeStorageAsync(new EdgeFormatStorageRequest
    {
      SourceId = cameraId, Command = body?.Command ?? string.Empty,
    }, Options(ct));
    return FromMutation(r);
  }

  // ── Import ───────────────────────────────────────────────────────────────────

  // The required permission depends on the destination (archive vs downloadable file), so the
  // action resolves it itself instead of declaring one up front.
  [HttpPost("cameras/{cameraId}/imports")]
  [CameraAccess(CameraAction.Control, Source = CameraIdSource.Action)]
  public async Task<IActionResult> StartImport(
    string cameraId, [FromBody] EdgeImportRequestDto body, CancellationToken ct)
  {
    if (body is null)
      return BadRequest(new { message = "request body is required" });

    // Writing into the archive is a Control action; producing a downloadable file is an Export one.
    var action = body.ExportToFile ? CameraAction.Export : CameraAction.Control;
    if (!await access.IsAllowedAsync(User, cameraId, action, ct))
      return Forbid();

    if (string.IsNullOrWhiteSpace(body.RecordingToken))
      return BadRequest(new { message = "recordingToken is required" });
    if (!TryParseUtc(body.StartTime, out var start) || !TryParseUtc(body.EndTime, out var end))
      return BadRequest(new { message = "startTime and endTime must be ISO 8601 timestamps" });
    if (end <= start)
      return BadRequest(new { message = "endTime must be after startTime" });
    // Without the recording origin the RTSP seek is forced to zero and a mid-recording window would
    // be stamped with the wrong wall clock — silently corrupting the archive timeline.
    if (!TryParseUtc(body.RecordingStartTime, out var recordingStart))
      return BadRequest(new { message = "recordingStartTime is required (ISO 8601)" });
    if (recordingStart > start)
      return BadRequest(new { message = "recordingStartTime must be at or before startTime" });

    var camera = await manager.Get(cameraId, ct);
    if (camera is null)
      return NotFound(new { message = $"camera '{cameraId}' not found" });
    if (string.IsNullOrWhiteSpace(camera.ServerId))
      return BadRequest(new { message = $"camera '{cameraId}' has no media server assigned" });

    var streamName = string.IsNullOrWhiteSpace(body.StreamName)
      ? camera.Streams.FirstOrDefault()?.StreamName
      : body.StreamName;
    if (string.IsNullOrWhiteSpace(streamName))
      return BadRequest(new { message = "streamName is required (camera has no configured streams)" });

    // Resolved here rather than cached: many devices hand out single-use replay URIs.
    var replay = await onvifClient.GetEdgeReplayUriAsync(new EdgeReplayUriRequest
    {
      SourceId = cameraId, RecordingToken = body.RecordingToken,
    }, Options(ct));
    if (string.IsNullOrWhiteSpace(replay.Uri))
      return StatusCode(StatusCodes.Status502BadGateway,
        new { message = string.IsNullOrWhiteSpace(replay.Message) ? "camera returned an empty replay uri" : replay.Message });

    // The window below is in server time, and the device indexes its card in its own clock. Without
    // a measured offset the two cannot be reconciled, and the pull would quietly land on a different
    // stretch of footage than the one asked for — worse than not pulling at all.
    if (!replay.HasClockOffsetMs)
      return StatusCode(StatusCodes.Status502BadGateway, new
      {
        message = $"camera '{cameraId}' does not report its clock (ONVIF GetSystemDateAndTime), "
          + "so the requested time range cannot be located on its storage",
      });

    var request = new Vms.Control.EdgeImportRequest
    {
      CameraId = cameraId,
      StreamName = streamName,
      ReplayUri = replay.Uri,
      StartUnixMs = ToUnixMs(start),
      EndUnixMs = ToUnixMs(end),
      RecordingStartUnixMs = ToUnixMs(recordingStart),
      ExportToFile = body.ExportToFile,
      SegmentSeconds = body.SegmentSeconds,
      ExportChunkSeconds = ExportChunkSeconds,
    };
    if (camera.Cred is { } cred)
      request.Cred = new Vms.Control.Credentials { User = cred.User ?? string.Empty, Pass = cred.Pass ?? string.Empty };

    try
    {
      var job = await GetMediaClient(camera.ServerId).StartEdgeImportAsync(request, Options(ct));
      return Accepted(ToJobDto(job));
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.FailedPrecondition)
    {
      return Conflict(new { message = ex.Status.Detail });
    }
  }

  [HttpGet("cameras/{cameraId}/imports")]
  [CameraAccess(CameraAction.ViewArchive)]
  public async Task<IActionResult> ListImports(string cameraId, CancellationToken ct)
  {
    var camera = await manager.Get(cameraId, ct);
    if (camera is null || string.IsNullOrWhiteSpace(camera.ServerId))
      return NotFound(new { message = $"camera '{cameraId}' not found" });

    var response = await GetMediaClient(camera.ServerId)
      .ListEdgeImportJobsAsync(new ListEdgeImportJobsRequest { CameraId = cameraId }, Options(ct));
    return Ok(response.Jobs.Select(ToJobDto).ToList());
  }

  [HttpDelete("cameras/{cameraId}/imports/{jobId}")]
  [CameraAccess(CameraAction.Control)]
  public async Task<IActionResult> CancelImport(string cameraId, string jobId, CancellationToken ct)
  {
    var camera = await manager.Get(cameraId, ct);
    if (camera is null || string.IsNullOrWhiteSpace(camera.ServerId))
      return NotFound(new { message = $"camera '{cameraId}' not found" });

    var client = GetMediaClient(camera.ServerId);

    // Job ids are opaque and global to the media server, and permission was granted for THIS
    // camera — without this check, Control on one camera would cancel another camera's import on
    // the same node.
    EdgeImportJob job;
    try
    {
      job = await client.GetEdgeImportJobAsync(new EdgeImportJobKey { JobId = jobId }, Options(ct));
    }
    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.NotFound)
    {
      return NotFound(new { message = "unknown import job" });
    }
    if (!string.Equals(job.CameraId, cameraId, StringComparison.Ordinal))
      return NotFound(new { message = "unknown import job" });

    var r = await client.CancelEdgeImportAsync(new EdgeImportJobKey { JobId = jobId }, Options(ct));
    return r.Success ? Ok(new { message = r.Message }) : NotFound(new { message = r.Message });
  }

  // ── Exports ──────────────────────────────────────────────────────────────────

  [HttpGet("cameras/{cameraId}/exports")]
  [CameraAccess(CameraAction.Export)]
  public async Task<IActionResult> ListExports(string cameraId, CancellationToken ct)
  {
    var results = await registry.FanOutFsAsync(
      (client, options) => client.ListExportFilesAsync(new ListExportFilesRequest { CameraId = cameraId }, options).ResponseAsync,
      ct);

    return Ok(results
      .SelectMany(r => r.Value.Files)
      .Select(f => new EdgeExportDto
      {
        RelPath = f.RelPath,
        CameraId = f.CameraId,
        SizeBytes = f.SizeBytes,
        CreatedAt = f.CreatedTs?.ToDateTime(),
      })
      .OrderByDescending(f => f.CreatedAt)
      .ToList());
  }

  /// <summary>
  /// Mints a short-lived URL for an exported file. A media element cannot send an Authorization
  /// header, so playing an export directly needs the grant in the URL itself.
  /// </summary>
  [HttpPost("export-link")]
  [CameraAccess(CameraAction.Export, Source = CameraIdSource.Action)]
  public async Task<IActionResult> CreateExportLink([FromQuery] string relPath, CancellationToken ct)
  {
    var cameraId = CameraIdOfExport(relPath);
    if (cameraId is null || !await access.IsAllowedAsync(User, cameraId, CameraAction.Export, ct))
      return NotFound();

    var expires = DateTime.UtcNow.Add(EdgeExportToken.Lifetime);
    var token = exportToken.Seal(relPath, User.Identity?.Name ?? string.Empty, expires);
    return Ok(new EdgeExportLinkDto
    {
      Url = $"/api/edge-archive/exports/{relPath}?token={Uri.EscapeDataString(token)}",
      ExpiresAt = expires,
    });
  }

  /// <summary>
  /// Streams an exported mp4, with Range support so the browser can seek without pulling the whole
  /// file. Authorized either by the caller's own permission or by a sealed link token; the camera id
  /// is the first path segment, which is what makes the check possible with no stored job state.
  /// </summary>
  [HttpGet("exports/{**relPath}")]
  [AllowAnonymous]
  [CameraAccess(CameraAction.Export, Source = CameraIdSource.Action)]
  public async Task<IActionResult> DownloadExport(
    string relPath, [FromQuery] string? token, [FromQuery] bool download, CancellationToken ct)
  {
    var cameraId = CameraIdOfExport(relPath);
    if (cameraId is null)
      return NotFound();

    // An unknown export and a forbidden one answer the same way: a 403 would confirm that someone
    // else's export exists at that path.
    var allowed = !string.IsNullOrEmpty(token)
      ? exportToken.IsValidFor(token, relPath)
      : await access.IsAllowedAsync(User, cameraId, CameraAction.Export, ct);
    if (!allowed)
      return NotFound();

    var owner = (await registry.FanOutFsAsync(
      (client, options) => client.StatExportFileAsync(new ExportFileRequest { RelPath = relPath }, options).ResponseAsync,
      ct)).FirstOrDefault(r => r.Value.Exists);

    if (owner is null)
      return NotFound();

    var size = (long)owner.Value.SizeBytes;
    var (offset, length) = ParseRange(Request.Headers.Range, size);
    if (length < 0)
    {
      Response.Headers.ContentRange = $"bytes */{size}";
      return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
    }

    Response.ContentType = "video/mp4";
    Response.Headers.AcceptRanges = "bytes";
    Response.ContentLength = length;
    // Inline by default so the player can use the same URL; only an explicit download detaches it.
    Response.Headers.ContentDisposition =
      $"{(download ? "attachment" : "inline")}; filename=\"{Path.GetFileName(relPath)}\"";

    if (offset > 0 || length != size)
    {
      Response.StatusCode = StatusCodes.Status206PartialContent;
      Response.Headers.ContentRange = $"bytes {offset}-{offset + length - 1}/{size}";
    }

    using var call = owner.Client.ReadExportFile(
      new ReadExportFileRequest { RelPath = relPath, Offset = (ulong)offset, Length = (ulong)length },
      cancellationToken: ct);
    await foreach (var chunk in call.ResponseStream.ReadAllAsync(ct))
      await Response.Body.WriteAsync(chunk.Data.Memory, ct);

    return new EmptyResult();
  }

  private static string? CameraIdOfExport(string? relPath)
  {
    var cameraId = relPath?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    return string.IsNullOrWhiteSpace(cameraId) ? null : cameraId;
  }

  /// <summary>
  /// Single `bytes=` range against a known size. Returns the whole file when no usable range was
  /// asked for, and a negative length when the request cannot be satisfied.
  /// </summary>
  private static (long Offset, long Length) ParseRange(string? header, long size)
  {
    if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
      return (0, size);

    var spec = header["bytes=".Length..].Split(',')[0].Trim();
    var dash = spec.IndexOf('-');
    if (dash < 0)
      return (0, size);

    var fromText = spec[..dash];
    var toText = spec[(dash + 1)..];

    if (fromText.Length == 0)
    {
      // Suffix form: the last N bytes.
      if (!long.TryParse(toText, out var suffix) || suffix <= 0)
        return (0, size);
      var start = Math.Max(0, size - suffix);
      return (start, size - start);
    }

    if (!long.TryParse(fromText, out var offset) || offset < 0)
      return (0, size);
    if (offset >= size)
      return (0, -1);

    var end = toText.Length > 0 && long.TryParse(toText, out var parsedEnd)
      ? Math.Min(parsedEnd, size - 1)
      : size - 1;
    return end < offset ? (0, -1) : (offset, end - offset + 1);
  }

  // ── Plumbing ─────────────────────────────────────────────────────────────────

  private static CameraControlService.CameraControlServiceClient GetMediaClient(string serverId) =>
    new(DaprClient.CreateInvocationInvoker(serverId));

  private IActionResult FromMutation(EdgeMutationResponse r) =>
    r.Success ? Ok(new { success = true }) : StatusCode(StatusCodes.Status502BadGateway, new { message = r.Error });

  private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

  private static bool TryParseUtc(string? value, out DateTime parsed)
  {
    parsed = default;
    if (string.IsNullOrWhiteSpace(value)) return false;
    if (!DateTime.TryParse(value, null,
        System.Globalization.DateTimeStyles.AdjustToUniversal |
        System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
      return false;
    parsed = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    return true;
  }

  private static EdgeImportJobDto ToJobDto(EdgeImportJob job) => new()
  {
    JobId = job.JobId,
    CameraId = job.CameraId,
    StreamName = job.StreamName,
    State = job.State switch
    {
      EdgeImportState.Running => "running",
      EdgeImportState.Completed => "completed",
      EdgeImportState.Failed => "failed",
      EdgeImportState.Cancelled => "cancelled",
      _ => "unknown",
    },
    StartTime = FromUnixMs(job.StartUnixMs),
    EndTime = FromUnixMs(job.EndUnixMs),
    Position = FromUnixMs(job.PositionUnixMs),
    ClipsWritten = job.ClipsWritten,
    BytesWritten = job.BytesWritten,
    Message = job.Message,
    ExportPath = job.ExportPath,
  };

  private static DateTime? FromUnixMs(long ms) =>
    ms <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
