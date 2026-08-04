using Grpc.Core;
using Vms.Onvif;

namespace VmsOnvif;

/// <summary>
/// Profile G handlers: the camera's own recordings on its SD card.
/// </summary>
/// <remarks>
/// Reads never throw for an unsupported camera — they answer with <c>message</c> set, because most
/// cameras in a deployment cannot do Profile G and the UI must distinguish that from a real fault.
/// Writes act on the device and report failure in <c>error</c> rather than as a transport error, so
/// the frontend can surface the device's own wording.
/// </remarks>
public partial class OnvifServiceImpl
{
  public override async Task<EdgeCapabilitiesResponse> GetEdgeCapabilities(
    CameraRequest request, ServerCallContext context)
  {
    var (caps, message) = await settingsService.GetEdgeCapabilitiesAsync(request.SourceId, context.CancellationToken);
    if (caps == null)
      return new EdgeCapabilitiesResponse { Message = message };

    var response = new EdgeCapabilitiesResponse
    {
      Recording            = caps.Recording,
      Search               = caps.Search,
      Replay               = caps.Replay,
      DynamicRecordings    = caps.DynamicRecordings,
      DynamicTracks        = caps.DynamicTracks,
      StorageConfiguration = caps.StorageConfiguration,
      AuxiliaryCommands    = caps.AuxiliaryCommands,
      MaxRecordings        = (uint)Math.Max(0, caps.MaxRecordings),
      Message              = message,
    };
    response.SupportedAuxiliaryCommands.AddRange(caps.SupportedAuxiliaryCommands);
    return response;
  }

  public override async Task<EdgeRecordingSummaryResponse> GetEdgeRecordingSummary(
    CameraRequest request, ServerCallContext context)
  {
    var (summary, offset, message) = await settingsService.GetEdgeRecordingSummaryAsync(
      request.SourceId, context.CancellationToken);
    var response = new EdgeRecordingSummaryResponse
    {
      DataFrom         = Iso(summary?.DataFrom),
      DataUntil        = Iso(summary?.DataUntil),
      NumberRecordings = (uint)Math.Max(0, summary?.NumberRecordings ?? 0),
      Supported        = summary != null,
      Message          = message,
    };
    if (offset is { } off)
      response.ClockOffsetMs = (long)off.TotalMilliseconds;
    return response;
  }

  public override async Task<ListEdgeRecordingsResponse> ListEdgeRecordings(
    CameraRequest request, ServerCallContext context)
  {
    var (recordings, offset, message) = await settingsService.ListEdgeRecordingsAsync(
      request.SourceId, context.CancellationToken);
    var response = new ListEdgeRecordingsResponse { Message = message };
    if (offset is { } recordingsOffset)
      response.ClockOffsetMs = (long)recordingsOffset.TotalMilliseconds;
    foreach (var rec in recordings)
    {
      var item = new EdgeRecording
      {
        RecordingToken    = rec.RecordingToken,
        SourceName        = rec.SourceName,
        SourceDescription = rec.SourceDescription,
        EarliestRecording = Iso(rec.EarliestRecording),
        LatestRecording   = Iso(rec.LatestRecording),
        Content           = rec.Content,
        RecordingStatus   = rec.RecordingStatus,
      };
      item.Tracks.AddRange(rec.Tracks.Select(t => new EdgeTrack
      {
        TrackToken  = t.TrackToken,
        TrackType   = t.TrackType,
        Description = t.Description,
        DataFrom    = Iso(t.DataFrom),
        DataTo      = Iso(t.DataTo),
      }));
      response.Recordings.Add(item);
    }
    return response;
  }

  public override async Task<EdgeIntervalsResponse> ListEdgeRecordingIntervals(
    EdgeIntervalsRequest request, ServerCallContext context)
  {
    if (!TryParseIso(request.StartTime, out var from))
      return new EdgeIntervalsResponse { Message = "start_time is not a valid ISO 8601 timestamp" };
    if (!TryParseIso(request.EndTime, out var to))
      return new EdgeIntervalsResponse { Message = "end_time is not a valid ISO 8601 timestamp" };

    var (intervals, searched, offset, message) = await settingsService.ListEdgeIntervalsAsync(
      request.SourceId, from, to, request.RecordingToken, (int)request.MaxResults, context.CancellationToken);

    var response = new EdgeIntervalsResponse { Searched = searched, Message = message };
    if (offset is { } off)
      response.ClockOffsetMs = (long)off.TotalMilliseconds;
    response.Intervals.AddRange(intervals.Select(i => new EdgeInterval
    {
      RecordingToken = i.RecordingToken,
      TrackToken     = i.TrackToken,
      StartTime      = Iso(i.From),
      EndTime        = Iso(i.Until),
    }));
    return response;
  }

  public override async Task<EdgeReplayUriResponse> GetEdgeReplayUri(
    EdgeReplayUriRequest request, ServerCallContext context)
  {
    if (string.IsNullOrWhiteSpace(request.RecordingToken))
      return new EdgeReplayUriResponse { Message = "recording_token is required" };

    var (uri, offset, message) = await settingsService.GetEdgeReplayUriAsync(
      request.SourceId, request.RecordingToken, request.Transport, context.CancellationToken);
    var response = new EdgeReplayUriResponse { Uri = uri, Message = message };
    if (offset is { } off)
      response.ClockOffsetMs = (long)off.TotalMilliseconds;
    return response;
  }

  public override async Task<EdgeRecordingJobsResponse> ListEdgeRecordingJobs(
    CameraRequest request, ServerCallContext context)
  {
    var (jobs, configurations, message) = await settingsService.ListEdgeRecordingJobsAsync(
      request.SourceId, context.CancellationToken);

    var response = new EdgeRecordingJobsResponse { Message = message };
    response.Jobs.AddRange(jobs.Select(j => new EdgeRecordingJob
    {
      JobToken       = j.JobToken,
      RecordingToken = j.RecordingToken,
      Mode           = j.Mode,
      SourceToken    = j.SourceToken,
    }));
    response.Configurations.AddRange(configurations.Select(c => new EdgeRecordingConfiguration
    {
      RecordingToken       = c.RecordingToken,
      SourceId             = c.SourceId,
      SourceName           = c.SourceName,
      SourceLocation       = c.SourceLocation,
      SourceDescription    = c.SourceDescription,
      SourceAddress        = c.SourceAddress,
      Content              = c.Content,
      MaximumRetentionTime = c.MaximumRetentionTime,
    }));
    return response;
  }

  public override Task<EdgeMutationResponse> SetEdgeRecordingJobMode(
    EdgeSetJobModeRequest request, ServerCallContext context)
    => Mutate(() => settingsService.SetEdgeRecordingJobModeAsync(
      request.SourceId, request.JobToken, request.Mode, context.CancellationToken));

  public override Task<EdgeMutationResponse> SetEdgeRecordingConfiguration(
    EdgeSetRecordingConfigurationRequest request, ServerCallContext context)
    => Mutate(() => settingsService.SetEdgeRecordingConfigurationAsync(
      request.SourceId, request.RecordingToken, request.Content, request.MaximumRetentionTime, context.CancellationToken));

  public override Task<EdgeMutationResponse> DeleteEdgeRecording(
    EdgeRecordingTokenRequest request, ServerCallContext context)
    => Mutate(() => settingsService.DeleteEdgeRecordingAsync(
      request.SourceId, request.RecordingToken, context.CancellationToken));

  public override Task<EdgeMutationResponse> DeleteEdgeTrack(
    EdgeTrackRequest request, ServerCallContext context)
    => Mutate(() => settingsService.DeleteEdgeTrackAsync(
      request.SourceId, request.RecordingToken, request.TrackToken, context.CancellationToken));

  public override Task<EdgeMutationResponse> DeleteEdgeRecordingJob(
    EdgeJobRequest request, ServerCallContext context)
    => Mutate(() => settingsService.DeleteEdgeRecordingJobAsync(
      request.SourceId, request.JobToken, context.CancellationToken));

  public override async Task<EdgeStorageResponse> ListEdgeStorage(
    CameraRequest request, ServerCallContext context)
  {
    var (configurations, supported, formatCommand, message) =
      await settingsService.ListEdgeStorageAsync(request.SourceId, context.CancellationToken);

    var response = new EdgeStorageResponse
    {
      Supported       = supported,
      FormatSupported = !string.IsNullOrWhiteSpace(formatCommand),
      FormatCommand   = formatCommand,
      Message         = message,
    };
    response.Configurations.AddRange(configurations.Select(c => new EdgeStorageConfiguration
    {
      Token      = c.Token,
      Type       = c.Type,
      LocalPath  = c.LocalPath,
      StorageUri = c.StorageUri,
      User       = c.User,
    }));
    return response;
  }

  public override Task<EdgeMutationResponse> FormatEdgeStorage(
    EdgeFormatStorageRequest request, ServerCallContext context)
    => Mutate(() => settingsService.FormatEdgeStorageAsync(
      request.SourceId, request.Command, context.CancellationToken));

  private static async Task<EdgeMutationResponse> Mutate(Func<Task> action)
  {
    try
    {
      await action();
      return new EdgeMutationResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new EdgeMutationResponse { Success = false, Error = ex.Message };
    }
  }

  private static string Iso(DateTime? value) =>
    value == null ? string.Empty : value.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

  // Empty means "unbounded" and is resolved against the camera's recording summary downstream —
  // only a non-empty value that fails to parse is an error.
  private static bool TryParseIso(string value, out DateTime? parsed)
  {
    parsed = null;
    if (string.IsNullOrWhiteSpace(value)) return true;
    if (!DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal
      | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
      return false;
    parsed = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    return true;
  }
}
