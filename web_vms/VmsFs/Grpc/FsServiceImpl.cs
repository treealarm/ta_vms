using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

public sealed class FsServiceImpl(ClipStorage storage, ArchiveFileStorage archiveStorage) : FsService.FsServiceBase
{
  private readonly ClipStorage _storage = storage;
  private readonly ArchiveFileStorage _archiveStorage = archiveStorage;

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

  public override Task<StatArchiveTsResponse> StatArchiveTs(
    StatArchiveTsRequest request, ServerCallContext context)
  {
    var abs = _archiveStorage.ResolveRelativePath(request.RelativePath);
    if (abs == null)
      return Task.FromResult(new StatArchiveTsResponse { Exists = false, Message = "invalid path" });

    try
    {
      var info = new FileInfo(abs);
      if (!info.Exists)
        return Task.FromResult(new StatArchiveTsResponse { Exists = false, Message = "not found" });
      return Task.FromResult(new StatArchiveTsResponse { Exists = true, SizeBytes = (ulong)info.Length });
    }
    catch (Exception ex)
    {
      return Task.FromResult(new StatArchiveTsResponse { Exists = false, Message = ex.Message });
    }
  }

  public override async Task ReadArchiveTs(
    ReadArchiveTsRequest request,
    IServerStreamWriter<ReadArchiveTsChunk> responseStream,
    ServerCallContext context)
  {
    var abs = _archiveStorage.ResolveRelativePath(request.RelativePath);
    if (abs == null)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid path"));

    var info = new FileInfo(abs);
    if (!info.Exists)
      throw new RpcException(new Status(StatusCode.NotFound, "not found"));

    var fileSize = (ulong)info.Length;
    var offset = request.Offset;
    if (offset > fileSize)
      throw new RpcException(new Status(StatusCode.OutOfRange, "offset past end"));

    var entityLen = fileSize - offset;
    if (request.Length > 0)
      entityLen = Math.Min(entityLen, request.Length);

    const int bufSize = 256 * 1024;
    var buf = new byte[bufSize];
    await using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, useAsync: true);
    fs.Seek((long)offset, SeekOrigin.Begin);

    var remaining = entityLen;
    while (remaining > 0)
    {
      var toRead = (int)Math.Min(remaining, (ulong)bufSize);
      var got = await fs.ReadAsync(buf.AsMemory(0, toRead), context.CancellationToken);
      if (got == 0)
        break;

      await responseStream.WriteAsync(
        new ReadArchiveTsChunk { Data = Google.Protobuf.ByteString.CopyFrom(buf, 0, got) },
        context.CancellationToken);
      remaining -= (ulong)got;
    }
  }

  public override async Task<GetArchiveTsClipIndexResponse> GetArchiveTsClipIndex(
    GetArchiveTsClipIndexRequest request, ServerCallContext context)
  {
    var abs = _archiveStorage.ResolveRelativePath(request.RelativePath);
    if (abs == null)
      return new GetArchiveTsClipIndexResponse { Found = false, Message = "invalid path" };

    var idxPath = abs + ".idx.json";
    if (!File.Exists(idxPath))
      return new GetArchiveTsClipIndexResponse { Found = false, Message = "index file not found" };

    try
    {
      var json = await File.ReadAllTextAsync(idxPath, context.CancellationToken);
      var idx = Google.Protobuf.JsonParser.Default.Parse<TsClipIndex>(json);
      return new GetArchiveTsClipIndexResponse { Found = true, Index = idx };
    }
    catch (Exception ex)
    {
      return new GetArchiveTsClipIndexResponse { Found = false, Message = ex.Message };
    }
  }

}
