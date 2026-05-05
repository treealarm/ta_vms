using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

public sealed class FsServiceImpl(ClipStorage storage) : FsService.FsServiceBase
{
  private readonly ClipStorage _storage = storage;

  public override async Task<OperationStatus> IngestClip(
    IngestClipRequest request,
    ServerCallContext context)
  {
    if (request.Clip is null
        || string.IsNullOrWhiteSpace(request.Clip.CameraId)
        || string.IsNullOrWhiteSpace(request.Clip.FilePath)
        || request.Clip.StartTs is null)
    {
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.PermanentFailure,
        Message = "camera_id, file_path and start_ts are required"
      };
    }

    try
    {
      var clipUid = await _storage.IngestAsync(request.Clip, context.CancellationToken);
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.Success,
        ClipUid = clipUid
      };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"IngestClip failed for '{request.Clip.FilePath}': {ex}");
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.RetryableFailure,
        Message = ex.Message
      };
    }
  }

  public override async Task<OperationStatus> RetainEvent(RetainEventRequest request, ServerCallContext context)
  {
    try
    {
      await _storage.RetainEventAsync(request, context.CancellationToken);
      return new OperationStatus { Status = OperationStatus.Types.Status.Success, Message = "ok" };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"RetainEvent failed: {ex}");
      return new OperationStatus { Status = OperationStatus.Types.Status.PermanentFailure, Message = ex.Message };
    }
  }

  public override async Task<GetRecordingsResponse> GetRecordings(
    GetRecordingsFromToRequest request,
    ServerCallContext context)
  {
    try
    {
      var list = await _storage.GetRecordingsFromToAsync(request, context.CancellationToken);
      var response = new GetRecordingsResponse();
      response.Recordings.AddRange(list);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordings failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<GetRecordingsResponse> GetRecordingsAround(
    GetRecordingsAroundRequest request,
    ServerCallContext context)
  {
    try
    {
      var list = await _storage.GetRecordingsAroundAsync(request, context.CancellationToken);
      var response = new GetRecordingsResponse();
      response.Recordings.AddRange(list);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordingsAround failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<GetRecordingRangesResponse> GetRecordingRanges(
    GetRecordingRangesRequest request,
    ServerCallContext context)
  {
    try
    {
      var ranges = await _storage.GetRecordingRangesAsync(request, context.CancellationToken);
      var response = new GetRecordingRangesResponse();
      response.Ranges.AddRange(ranges);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordingRanges failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<RecordingRangeForClipResponse> GetRecordingRangeForClip(
    GetRecordingRangeForClipRequest request,
    ServerCallContext context)
  {
    try
    {
      var (found, range, clipStart) = await _storage.GetRecordingRangeForClipAsync(request, context.CancellationToken);
      return new RecordingRangeForClipResponse { Found = found, Range = range, ClipStartTs = clipStart };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordingRangeForClip failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<ResolveRangePositionResponse> ResolveRangePosition(
    ResolveRangePositionRequest request,
    ServerCallContext context)
  {
    try
    {
      var (found, fp, ms) = await _storage.ResolveRangePositionAsync(request, context.CancellationToken);
      return new ResolveRangePositionResponse { Found = found, FilePath = fp, PositionMs = ms };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"ResolveRangePosition failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<GetRecordingsResponse> GetNextRecordingAfter(
    GetNextRecordingAfterRequest request,
    ServerCallContext context)
  {
    try
    {
      var list = await _storage.GetNextRecordingAfterAsync(request, context.CancellationToken);
      var response = new GetRecordingsResponse();
      response.Recordings.AddRange(list);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetNextRecordingAfter failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<GetCameraRetentionStatusResponse> GetCameraRetentionStatus(
    GetCameraRetentionStatusRequest request,
    ServerCallContext context)
  {
    try
    {
      var active = await _storage.HasActiveRetentionWindowAsync(request.CameraId, context.CancellationToken);
      return new GetCameraRetentionStatusResponse { ActiveRetentionWindow = active };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetCameraRetentionStatus failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<ListRetentionEventsResponse> ListRetentionEvents(
    ListRetentionEventsRequest request,
    ServerCallContext context)
  {
    try
    {
      var (events, hasMore, nextBefore) = await _storage.ListRetentionEventsAsync(
        request.Limit,
        request.BeforeAlarmStartTs,
        context.CancellationToken);

      var response = new ListRetentionEventsResponse { HasMore = hasMore };
      response.Events.AddRange(events);
      if (nextBefore != null)
        response.NextBeforeAlarmStartTs = nextBefore;
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"ListRetentionEvents failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

}
