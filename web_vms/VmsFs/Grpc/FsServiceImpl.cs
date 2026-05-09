using Grpc.Core;
using Vms.Fs;
using System.Linq;

namespace VmsFs;

public sealed class FsServiceImpl(
  ClipStorage storage,
  StorageListCache storageListCache,
  CameraStorageCache storageCache) : FsService.FsServiceBase
{
  private readonly ClipStorage _storage = storage;
  private readonly StorageListCache _storageListCache = storageListCache;

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


  public override async Task<GetRecordingsResponse> GetClipsFrom(
    GetClipsFromRequest request,
    ServerCallContext context)
  {
    try
    {
      var list = await _storage.GetClipsFromAsync(request, context.CancellationToken);
      var response = new GetRecordingsResponse();
      response.Recordings.AddRange(list);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetClipsFrom failed: {ex}");
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

  public override async Task<GetCameraStorageResponse> GetCameraStorage(
    GetCameraStorageRequest request,
    ServerCallContext context)
  {
    var info = await storageCache.GetAsync(request.CameraId, context.CancellationToken);
    var response = new GetCameraStorageResponse();
    if (info != null)
      response.Storage = info;
    return response;
  }

  public override async Task<GetRecordingByUidResponse> GetRecordingByUid(
    GetRecordingByUidRequest request,
    ServerCallContext context)
  {
    var uids = new List<Guid>(request.ClipUids.Count);
    foreach (var raw in request.ClipUids)
    {
      if (!Guid.TryParse(raw, out var uid))
        throw new RpcException(new Status(StatusCode.InvalidArgument, $"invalid clip_uid: {raw}"));
      uids.Add(uid);
    }

    var recordings = await _storage.GetRecordingsByUidsAsync(uids, context.CancellationToken);
    var response = new GetRecordingByUidResponse();
    response.Recordings.AddRange(recordings);
    return response;
  }

  public override async Task<StatArchiveTsResponse> StatArchiveTs(
    StatArchiveTsRequest request, ServerCallContext context)
  {
    if (!Guid.TryParse(request.ClipUid, out var uid))
      return new StatArchiveTsResponse { Exists = false, Message = "invalid clip_uid" };

    var recordings = await _storage.GetRecordingsByUidsAsync([uid], context.CancellationToken);
    var rec = recordings.FirstOrDefault();
    if (rec is null || string.IsNullOrWhiteSpace(rec.FilePath))
      return new StatArchiveTsResponse { Exists = false, Message = "clip not found" };

    var abs = await ResolveAbsolutePathAsync(rec.StorageId, rec.FilePath, context.CancellationToken);
    if (abs is null)
      return new StatArchiveTsResponse { Exists = false, Message = "storage not found" };

    try
    {
      var info = new FileInfo(abs);
      if (!info.Exists)
        return new StatArchiveTsResponse { Exists = false, Message = "not found" };
      return new StatArchiveTsResponse { Exists = true, SizeBytes = (ulong)info.Length, FilePath = abs };
    }
    catch (Exception ex)
    {
      return new StatArchiveTsResponse { Exists = false, Message = ex.Message };
    }
  }

  public override async Task ReadArchiveTs(
    ReadArchiveTsRequest request,
    IServerStreamWriter<ReadArchiveTsChunk> responseStream,
    ServerCallContext context)
  {
    if (!Guid.TryParse(request.ClipUid, out var uid))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid clip_uid"));

    var recordings = await _storage.GetRecordingsByUidsAsync([uid], context.CancellationToken);
    var rec = recordings.FirstOrDefault();
    if (rec is null || string.IsNullOrWhiteSpace(rec.FilePath))
      throw new RpcException(new Status(StatusCode.NotFound, "clip not found"));

    var abs = await ResolveAbsolutePathAsync(rec.StorageId, rec.FilePath, context.CancellationToken)
      ?? throw new RpcException(new Status(StatusCode.NotFound, "storage not found"));
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

  private async Task<string?> ResolveAbsolutePathAsync(string storageId, string relativePath, CancellationToken ct)
  {
    var storages = await _storageListCache.GetAsync(ct);
    var root = _storageListCache.Find(storages, storageId)?.RootPath;
    if (string.IsNullOrWhiteSpace(root))
      return null;
    return Path.Combine(root, relativePath);
  }

}
