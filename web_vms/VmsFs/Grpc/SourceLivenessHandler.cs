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
        s.RecoveredFrameTs?.ToDateTime(),
        s.RecordingExpected,
        context.CancellationToken);
    }

    return new OperationStatus { Status = OperationStatus.Types.Status.Success };
  }

  public override async Task<ListSourceOutagesResponse> ListSourceOutages(
    ListSourceOutagesRequest request, ServerCallContext context)
  {
    Guid? outageUid = request.HasOutageUid && Guid.TryParse(request.OutageUid, out var uid) ? uid : null;
    var rows = await outageStore.ListAsync(
      request.HasCameraId ? request.CameraId : null,
      request.FromTs?.ToDateTime(),
      request.ToTs?.ToDateTime(),
      (int)request.Limit,
      context.CancellationToken,
      outageUid);

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

  public override async Task<OperationStatus> UpdateSourceOutage(
    UpdateSourceOutageRequest request, ServerCallContext context)
  {
    if (!Guid.TryParse(request.OutageUid, out var uid))
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.PermanentFailure,
        Message = "outage_uid must be a uuid"
      };

    var updated = await outageStore.UpdateOutcomeAsync(
      uid, request.State, request.FilledMs, request.Attempts, request.Message,
      context.CancellationToken);

    return new OperationStatus
    {
      // Gone rather than broken: retention removes closed rows, and a caller reporting on one that
      // has just aged out should not be told the write failed.
      Status = updated ? OperationStatus.Types.Status.Success : OperationStatus.Types.Status.PermanentFailure,
      Message = updated ? string.Empty : "outage not found"
    };
  }
}
