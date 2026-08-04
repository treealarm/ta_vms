using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

/// <summary>
/// Receives source heartbeats from media_server. The only place where a report becomes an interval.
/// </summary>
public sealed partial class FsServiceImpl
{
  public override async Task<OperationStatus> ReportSourceLiveness(
    ReportSourceLivenessRequest request, ServerCallContext context)
  {
    foreach (var s in request.Streams)
    {
      await livenessTracker.ApplyAsync(
        s.CameraId,
        s.StreamName ?? string.Empty,
        s.Alive,
        s.LastFrameTs?.ToDateTime(),
        s.RecordingExpected,
        context.CancellationToken);
    }

    return new OperationStatus { Status = OperationStatus.Types.Status.Success };
  }

  public override async Task<ListSourceOutagesResponse> ListSourceOutages(
    ListSourceOutagesRequest request, ServerCallContext context)
  {
    var rows = await outageStore.ListAsync(
      request.HasCameraId ? request.CameraId : null,
      request.FromTs?.ToDateTime(),
      request.ToTs?.ToDateTime(),
      (int)request.Limit,
      context.CancellationToken);

    var response = new ListSourceOutagesResponse();
    foreach (var r in rows)
    {
      var row = new SourceOutageRow
      {
        OutageUid = r.OutageUid.ToString(),
        CameraId = r.CameraId,
        StreamName = r.StreamName,
        StartedTs = Timestamp.FromDateTime(DateTime.SpecifyKind(r.StartedTs, DateTimeKind.Utc)),
        Cause = r.Cause,
        RecordingExpected = r.RecordingExpected,
        State = r.State,
        FilledMs = r.FilledMs,
        Attempts = r.Attempts,
        Message = r.Message
      };
      if (r.EndedTs is { } ended)
        row.EndedTs = Timestamp.FromDateTime(DateTime.SpecifyKind(ended, DateTimeKind.Utc));
      response.Outages.Add(row);
    }
    return response;
  }
}
