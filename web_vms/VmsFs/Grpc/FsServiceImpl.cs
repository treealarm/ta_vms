using Dapr.Client;
using Grpc.Core;
using Vms.Fs;
using System.Linq;

namespace VmsFs;

public sealed class FsServiceImpl(
  ClipStorage storage,
  StorageListCache storageListCache,
  CameraStorageCache storageCache,
  AlarmRecordingService alarmRecording,
  FsActivityLog activityLog,
  FragIndexWarmer fragWarmer,
  DaprClient daprClient) : FsService.FsServiceBase
{
  private readonly ClipStorage _storage = storage;
  private readonly StorageListCache _storageListCache = storageListCache;
  private const string RetentionEventCreatedTopic = "retention-event-created";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  // Best-effort notification for the (optional) Square integration — VmsCfg is the only service
  // that talks to Square (see CLAUDE.md), so this just signals "a new alarm happened" and lets
  // VmsCfg's SquareAlarmSubscriber pull it from there. Harmless if nothing is subscribed.
  private void NotifyRetentionEventCreated(string cameraId, string? streamName, DateTime alarmStartTs)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        await daprClient.PublishEventAsync(PubSubName, RetentionEventCreatedTopic,
          new { CameraId = cameraId, StreamName = streamName, AlarmStartTs = alarmStartTs });
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"retention-event-created publish failed for '{cameraId}': {ex}");
      }
    });
  }

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
      // A finalized clip (end_ts set) has its full mfra written, so warm its fragment index now to
      // spare the first playback the parse. Best-effort: dropped if the warmer queue is full.
      if (request.Clip.EndTs is not null && Guid.TryParse(clipUid, out var uid))
        fragWarmer.Enqueue(uid);
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
      var (eventUuid, until) = await _storage.RetainEventAsync(request, context.CancellationToken);
      var streamName = request.HasStreamName && !string.IsNullOrWhiteSpace(request.StreamName) ? request.StreamName : null;
      _ = alarmRecording.TryActivateAsync(request.CameraId, streamName, until, CancellationToken.None);
      NotifyRetentionEventCreated(request.CameraId, streamName, request.AlarmStartTs.ToDateTime());
      return new OperationStatus { Status = OperationStatus.Types.Status.Success, Message = "ok", EventUuid = eventUuid.ToString() };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"RetainEvent failed: {ex}");
      return new OperationStatus { Status = OperationStatus.Types.Status.RetryableFailure, Message = ex.Message };
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


  public override async Task<GetRecordingCoverageResponse> GetRecordingCoverage(
    GetRecordingCoverageRequest request,
    ServerCallContext context)
  {
    try
    {
      var buckets = await _storage.GetRecordingCoverageAsync(request, context.CancellationToken);
      var response = new GetRecordingCoverageResponse();
      response.Buckets.AddRange(buckets);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordingCoverage failed: {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }

  public override async Task<GetRecordingTimelineResponse> GetRecordingTimeline(
    GetRecordingTimelineRequest request,
    ServerCallContext context)
  {
    try
    {
      var tracks = await _storage.GetRecordingTimelineAsync(request, context.CancellationToken);
      var response = new GetRecordingTimelineResponse();
      response.Tracks.AddRange(tracks);
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordingTimeline failed: {ex}");
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
      var (allStreams, streams) = await _storage.GetRetainedStreamsAsync(request.CameraId, context.CancellationToken);
      var resp = new GetCameraRetentionStatusResponse
      {
        ActiveRetentionWindow = allStreams || streams.Count > 0,
        AllStreamsRetained = allStreams,
      };
      resp.RetainedStreams.AddRange(streams);
      return resp;
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
      // event_ids set => the gateway is completing groups; return all their episodes, unpaged.
      if (request.EventIds.Count > 0)
      {
        var all = await _storage.GetEpisodesByEventIdsAsync(request.EventIds, context.CancellationToken);
        var byIds = new ListRetentionEventsResponse { HasMore = false };
        byIds.Events.AddRange(all);
        return byIds;
      }

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
    var all = await storageCache.GetAllAsync(request.CameraId, context.CancellationToken);
    var response = new GetCameraStorageResponse();
    if (all.Count > 0)
      response.Storage = all[0];
    response.Storages.AddRange(all);
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

  public override async Task<GetRecordingsResponse> GetClipsForEvent(
    GetClipsForEventRequest request,
    ServerCallContext context)
  {
    if (!Guid.TryParse(request.EventUuid, out var eventUuid))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "valid event_uuid is required"));

    var clips = await _storage.GetClipsForEventAsync(eventUuid, context.CancellationToken);
    if (clips is null)
      throw new RpcException(new Status(StatusCode.NotFound, "event not found"));

    var resp = new GetRecordingsResponse();
    resp.Recordings.AddRange(clips);
    return resp;
  }

  public override async Task<GetClipIndexesResponse> GetClipIndexes(
    GetClipIndexesRequest request, ServerCallContext context)
  {
    var ct = context.CancellationToken;
    var resp = new GetClipIndexesResponse();

    var uids = new List<Guid>();
    foreach (var s in request.ClipUids)
      if (Guid.TryParse(s, out var g)) uids.Add(g);

    var clips = await _storage.GetClipsForIndexAsync(uids, ct);

    // Cached entries deserialize instantly; uncached ones are parsed from the local file (bounded
    // concurrency — these are cheap local reads) and persisted so later playbacks skip the parse.
    var indexByUid = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ClipIndexParser.SegmentIndex>();
    using var gate = new SemaphoreSlim(8);
    await Task.WhenAll(clips.Select(async kv =>
    {
      var (uid, info) = (kv.Key, kv.Value);
      var idx = info.FragIndex is not null ? ClipIndexParser.Deserialize(info.FragIndex) : null;
      if (idx is null)
      {
        await gate.WaitAsync(ct);
        try
        {
          var abs = await ResolveAbsolutePathAsync(info.StorageId, info.FilePath, ct);
          if (abs is not null)
          {
            idx = await ClipIndexParser.ReadAsync(abs, ct);
            if (idx is not null)
              await _storage.SaveFragIndexAsync(uid, ClipIndexParser.Serialize(idx), ct);
          }
        }
        finally { gate.Release(); }
      }
      if (idx is not null) indexByUid[uid] = idx;
    }));

    foreach (var s in request.ClipUids)
    {
      var ci = new ClipIndex { ClipUid = s };
      if (Guid.TryParse(s, out var uid) && indexByUid.TryGetValue(uid, out var idx))
      {
        ci.Found = true;
        ci.InitSize = idx.InitSize;
        foreach (var f in idx.Fragments)
          ci.Fragments.Add(new ClipFragment { Offset = f.Offset, Size = f.Size, Duration = f.Duration });
      }
      resp.Indexes.Add(ci);
    }
    return resp;
  }

  public override Task<GetFsActivityResponse> GetFsActivity(
    GetFsActivityRequest request,
    ServerCallContext context)
  {
    var limit = (int)(request.Limit == 0 ? 100u : request.Limit);
    var entries = activityLog.GetRecent(limit);
    var response = new GetFsActivityResponse();
    foreach (var e in entries)
    {
      var evt = new FsFileEvent
      {
        ClipUid    = e.ClipUid.ToString(),
        FilePath   = e.FilePath,
        CameraId   = e.CameraId,
        StreamName = e.StreamName,
        Retained   = e.Retained,
        SizeBytes  = e.SizeBytes,
        OccurredAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(e.OccurredAt),
        EventType  = e.EventType,
      };
      if (e.EndTs.HasValue)
        evt.EndTs = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(e.EndTs.Value);
      response.Events.Add(evt);
    }
    return Task.FromResult(response);
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
