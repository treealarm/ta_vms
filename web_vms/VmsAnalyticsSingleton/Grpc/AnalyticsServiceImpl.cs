using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Vms.Analytics;
// The local ring-buffer record VmsAnalyticsSingleton.AnalyticsActivityEntry shadows the proto message
// of the same name inside this namespace — alias the proto one explicitly.
using ProtoActivityEntry = Vms.Analytics.AnalyticsActivityEntry;

namespace VmsAnalyticsSingleton;

public sealed class AnalyticsServiceImpl(
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  AnalyticsActivityLog activityLog,
  LiveDetectionBroadcaster liveBroadcaster,
  LiveEventBroadcaster liveEventBroadcaster,
  MetadataArchiveWriter metadataWriter,
  CameraStorageResolver storageResolver,
  AnalyticsWorkerRegistry? workers = null,
  RecentLiveEmbeddingCache? recentEmbeddings = null)
  : VmsAnalyticsService.VmsAnalyticsServiceBase
{
  private readonly float _defaultMinScore = ResolveDefaultMinScore();
  private const int MaxSearchCandidates = 20_000;
  // Body ReID is stable across a walk-through; a five-minute gallery of the same track is useful.
  private const int MaxProbeExemplars = 16;
  private static readonly TimeSpan ProbeExemplarWindow = TimeSpan.FromMinutes(5);
  // Face is not. Track ids recycle and a person-box can pick up a second face a few seconds later;
  // OR-ing those vectors (max cosine) searches for several identities at once. Stay close to the click
  // and keep only embeddings that still look like the nearest sample.
  private const int MaxFaceProbeExemplars = 4;
  private static readonly TimeSpan FaceProbeWindow = TimeSpan.FromSeconds(30);
  private const float FaceProbeSelfMin = 0.40f;

  // Boxes the camera reported itself, already parsed and normalized by VmsOnvif (which owns the
  // ONVIF schema knowledge). Straight into the same fan-out the CV pipeline feeds, plus the archive
  // writer's queue. The reply still carries nothing and the call is still not retried: both
  // destinations are best-effort, and a metadata document that arrives late is worthless to the
  // overlay and unattributable to a clip.
  public override Task<PushOnvifDetectionsResponse> PushOnvifDetections(
    OnvifDetectionBatch request, ServerCallContext context)
  {
    foreach (var d in request.Detections)
    {
      var kind = DetectionKinds.ToDbString(d.Kind);
      var detectedAt = d.DetectedAt?.ToDateTime() ?? DateTime.UtcNow;
      var label = d.HasLabel ? d.Label : null;
      var plateText = d.HasPlateText ? d.PlateText : null;

      liveBroadcaster.Publish(new LiveDetection(
        d.CameraId, d.TrackId, kind, d.Confidence,
        d.Bbox?.X ?? 0, d.Bbox?.Y ?? 0, d.Bbox?.Width ?? 0, d.Bbox?.Height ?? 0,
        detectedAt, plateText,
        Persisted: false, ObjectId: null, Source: "onvif",
        Label: label));

      metadataWriter.Offer(new MetadataSample(
        d.CameraId, d.StreamName ?? string.Empty, "onvif", detectedAt,
        d.TrackId, kind, d.Confidence,
        d.Bbox?.X ?? 0, d.Bbox?.Y ?? 0, d.Bbox?.Width ?? 0, d.Bbox?.Height ?? 0,
        label, plateText));
    }
    return Task.FromResult(new PushOnvifDetectionsResponse());
  }

  // ONVIF events forwarded by VmsOnvif. Same relay shape as PushOnvifDetections: nothing is
  // stored, and the reply is empty because the caller is fire-and-forget.
  public override Task<PushOnvifEventsResponse> PushOnvifEvents(
    OnvifEventBatch request, ServerCallContext context)
  {
    foreach (var e in request.Events)
    {
      liveEventBroadcaster.Publish(new LiveCameraEvent(
        e.CameraId, e.Topic, e.Label, e.Active,
        e.ReceivedAt?.ToDateTime() ?? DateTime.UtcNow, e.Matched));
    }
    return Task.FromResult(new PushOnvifEventsResponse());
  }

  public override async Task StreamCameraEvents(
    StreamCameraEventsRequest request,
    IServerStreamWriter<LiveEvent> responseStream,
    ServerCallContext context)
  {
    var cameraId = string.IsNullOrEmpty(request.CameraId) ? null : request.CameraId;
    await foreach (var e in liveEventBroadcaster.Subscribe(cameraId, context.CancellationToken))
    {
      await responseStream.WriteAsync(new LiveEvent
      {
        CameraId   = e.CameraId,
        Topic      = e.Topic,
        Label      = e.Label,
        Active     = e.Active,
        Matched    = e.Matched,
        ReceivedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.ReceivedAt, DateTimeKind.Utc)),
      });
    }
  }

  public override Task<GetAnalyticsActivityResponse> GetAnalyticsActivity(
    GetAnalyticsActivityRequest request, ServerCallContext context)
  {
    var limit = request.Limit > 0 ? (int)request.Limit : 50;
    var response = new GetAnalyticsActivityResponse();
    foreach (var e in activityLog.GetRecent(limit))
    {
      response.Events.Add(ToProto(e));
    }
    return Task.FromResult(response);
  }

  public override async Task StreamCameraDetections(
    StreamCameraDetectionsRequest request,
    IServerStreamWriter<ProtoActivityEntry> responseStream,
    ServerCallContext context)
  {
    // Empty camera_id means "all cameras" — the frontend multiplexes one stream for the whole
    // monitor rather than opening one connection per camera. crop_jpeg is never populated (the
    // broadcaster already carries a geometry-only projection).
    var cameraId = string.IsNullOrEmpty(request.CameraId) ? null : request.CameraId;
    await foreach (var e in liveBroadcaster.Subscribe(cameraId, context.CancellationToken))
    {
      var entry = new ProtoActivityEntry
      {
        CameraId   = e.CameraId,
        TrackId    = e.TrackId,
        Kind       = DetectionKinds.Parse(e.Kind),
        Confidence = e.Confidence,
        Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
        DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
        Persisted  = e.Persisted,
        Source     = e.Source,
      };
      if (e.PlateText is not null) entry.PlateText = e.PlateText;
      if (e.ObjectId is not null) entry.ObjectId = e.ObjectId;
      if (e.Label is not null) entry.Label = e.Label;
      await responseStream.WriteAsync(entry);
    }
  }

  private static ProtoActivityEntry ToProto(AnalyticsActivityEntry e)
  {
    var entry = new ProtoActivityEntry
    {
      CameraId   = e.CameraId,
      TrackId    = e.TrackId,
      Kind       = DetectionKinds.Parse(e.Kind),
      Confidence = e.Confidence,
      Bbox       = new BoundingBox { X = e.BboxX, Y = e.BboxY, Width = e.BboxWidth, Height = e.BboxHeight },
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(e.DetectedAt, DateTimeKind.Utc)),
      CropJpeg   = Google.Protobuf.ByteString.CopyFrom(e.CropJpeg),
      Persisted  = e.Persisted,
      // The ring buffer only ever holds CV detections — ONVIF metadata is published straight to
      // the live overlay and never recorded here.
      Source     = "cv",
    };
    if (e.PlateText is not null) entry.PlateText = e.PlateText;
    return entry;
  }

  public override async Task<ListDetectionsResponse> ListDetections(
    ListDetectionsRequest request, ServerCallContext context)
  {
    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var query = db.Detections.AsQueryable();

    if (request.HasCameraId && !string.IsNullOrWhiteSpace(request.CameraId))
      query = query.Where(d => d.CameraId == request.CameraId);
    // Caller's visible set, applied in SQL so a restricted user still gets full pages.
    if (request.CameraIds.Count > 0)
    {
      var visible = request.CameraIds.ToList();
      query = query.Where(d => visible.Contains(d.CameraId));
    }
    if (request.HasEventId && !string.IsNullOrWhiteSpace(request.EventId))
      query = query.Where(d => d.EventId == request.EventId);
    if (request.HasEventUuid && Guid.TryParse(request.EventUuid, out var eventUuid))
      query = query.Where(d => d.EventUuid == eventUuid);
    if (request.FromTs is not null)
    {
      var from = request.FromTs.ToDateTime();
      query = query.Where(d => d.DetectedAt >= from);
    }
    if (request.ToTs is not null)
    {
      var to = request.ToTs.ToDateTime();
      query = query.Where(d => d.DetectedAt <= to);
    }
    if (request.HasPlateText && !string.IsNullOrWhiteSpace(request.PlateText))
      query = query.Where(d => d.PlateText == request.PlateText);

    // Limit == 0 (proto default) must not mean "the whole table" — cap it server-side.
    query = query
      .OrderByDescending(d => d.DetectedAt)
      .Take(request.Limit > 0 ? (int)request.Limit : 100);

    var rows = await query.ToListAsync(context.CancellationToken);
    var response = new ListDetectionsResponse();
    response.Detections.AddRange(rows.Select(ToProto));
    return response;
  }

  public override async Task<GetDetectionCropResponse> GetDetectionCrop(
    GetDetectionCropRequest request, ServerCallContext context)
  {
    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var row = await db.Detections.FindAsync([request.Id], context.CancellationToken);
    if (row is null)
      return new GetDetectionCropResponse();

    // Always report the owning camera, even when there is no crop to return: the gateway decides
    // whether this caller may see the detection at all before it looks at the bytes.
    var response = new GetDetectionCropResponse { CameraId = row.CameraId };

    if (row.StorageId is null || row.CropPath is null)
      return response;

    var root = await storageResolver.GetRootPathAsync(row.StorageId, context.CancellationToken);
    if (root is null)
      return response;

    var fullPath = Path.Combine(root, row.CropPath);
    if (!File.Exists(fullPath))
      return response;

    response.Jpeg = Google.Protobuf.ByteString.CopyFrom(
      await File.ReadAllBytesAsync(fullPath, context.CancellationToken));
    return response;
  }

  public override async Task<SearchSimilarDetectionsResponse> SearchSimilarDetections(
    SearchSimilarDetectionsRequest request, ServerCallContext context)
  {
    if (request.ProbeCase == SearchSimilarDetectionsRequest.ProbeOneofCase.None)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "exactly one probe must be set"));
    if (request.FromTs is null || request.ToTs is null)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "from_ts and to_ts are required"));

    var fromTs = request.FromTs.ToDateTime();
    var toTs = request.ToTs.ToDateTime();
    if (fromTs > toTs)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "from_ts must be <= to_ts"));

    await using var db = await dbFactory.CreateDbContextAsync(context.CancellationToken);
    var probe = await ResolveProbeAsync(db, request, context.CancellationToken);

    var minScore = request.HasMinScore ? Math.Clamp(request.MinScore, -1f, 1f) : _defaultMinScore;
    var limit = request.Limit > 0 ? Math.Min((int)request.Limit, 500) : 100;
    var visibleCameraIds = request.CameraIds.Distinct().ToList();

    var query = from d in db.Detections
      join e in db.DetectionEmbeddings on d.Id equals e.DetectionId
      where d.Kind == probe.Kind
            && d.DetectedAt >= fromTs
            && d.DetectedAt <= toTs
            && e.Kind == probe.Kind
      select new CandidateProjection
      {
        Detection = d,
        Dim = e.Dim,
        Embedding = e.Embedding,
      };

    if (visibleCameraIds.Count > 0)
      query = query.Where(x => visibleCameraIds.Contains(x.Detection.CameraId));

    // Keep the probe detection in the hit list (score ≈ 1). Excluding it left operators on a
    // weaker neighbour that often sits in a recording gap while the clicked instant still has
    // archive — and with sparse face galleries that neighbour was sometimes the only row.

    // Do not ORDER BY detected_at DESC LIMIT n before scoring: that silently drops the older
    // half of the operator's own window on a busy site, and "expand the period" makes it worse.
    var candidateCount = await query.CountAsync(context.CancellationToken);
    if (candidateCount > MaxSearchCandidates)
      throw new RpcException(new Status(StatusCode.FailedPrecondition, "too many candidates"));

    var candidates = await query.ToListAsync(context.CancellationToken);

    var hits = new List<(DbDetection Row, float Score)>(Math.Min(limit, candidates.Count));
    foreach (var candidate in candidates)
    {
      var embedding = EmbeddingCodec.DecodeNormalized(candidate.Embedding, candidate.Dim);
      if (embedding.Length == 0)
        continue;

      var score = EmbeddingCodec.MaxCosine(probe.Embeddings, embedding);
      if (score < minScore)
        continue;
      hits.Add((candidate.Detection, score));
    }

    // Dense same-camera galleries bury other angles: for this person probe the first other-camera
    // hit sat at rank ~370 while limit is 200. Round-robin by camera after the score filter so
    // every camera that clears min_score gets into the page; the UI then groups by track.
    var response = new SearchSimilarDetectionsResponse();
    foreach (var hit in DiversifyHitsByCamera(hits, limit))
    {
      response.Hits.Add(new SimilarDetectionHit
      {
        Detection = ToProto(hit.Row),
        Score = hit.Score,
      });
    }
    return response;
  }

  /// <summary>
  /// Interleave top-scoring hits across cameras. Plain global top-K by score fills the limit with
  /// near-duplicates from the probe camera when embeddings are stored densely.
  /// </summary>
  public static List<(DbDetection Row, float Score)> DiversifyHitsByCamera(
    List<(DbDetection Row, float Score)> hits, int limit)
  {
    if (hits.Count == 0 || limit <= 0)
      return [];

    var byCamera = hits
      .GroupBy(h => h.Row.CameraId)
      .ToDictionary(
        g => g.Key,
        g => g.OrderByDescending(x => x.Score).ThenBy(x => x.Row.DetectedAt).ToList());

    // Strongest camera first in the rotation so the probe camera still leads, but others follow
    // immediately instead of waiting until rank 300+.
    var camOrder = byCamera
      .OrderByDescending(kv => kv.Value[0].Score)
      .Select(kv => kv.Key)
      .ToList();

    var cursors = camOrder.ToDictionary(c => c, _ => 0);
    var selected = new List<(DbDetection Row, float Score)>(Math.Min(limit, hits.Count));
    while (selected.Count < limit)
    {
      var progressed = false;
      foreach (var cam in camOrder)
      {
        var list = byCamera[cam];
        var i = cursors[cam];
        if (i >= list.Count)
          continue;
        selected.Add(list[i]);
        cursors[cam] = i + 1;
        progressed = true;
        if (selected.Count >= limit)
          break;
      }
      if (!progressed)
        break;
    }
    return selected;
  }

  private async Task<ResolvedProbe> ResolveProbeAsync(
    AnalyticsDbContext db, SearchSimilarDetectionsRequest request, CancellationToken ct)
  {
    return request.ProbeCase switch
    {
      SearchSimilarDetectionsRequest.ProbeOneofCase.DetectionProbe =>
        await ResolveDetectionProbeAsync(db, request.DetectionProbe.DetectionId, ct),
      SearchSimilarDetectionsRequest.ProbeOneofCase.ArchiveProbe =>
        await ResolveArchiveProbeAsync(db, request.ArchiveProbe, ct),
      SearchSimilarDetectionsRequest.ProbeOneofCase.ImageProbe =>
        await ResolveImageProbeAsync(request.ImageProbe, ct),
      _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "unknown probe type")),
    };
  }

  private async Task<ResolvedProbe> ResolveDetectionProbeAsync(
    AnalyticsDbContext db, long detectionId, CancellationToken ct)
  {
    var source = await db.Detections
      .Where(d => d.Id == detectionId)
      .Select(d => new { d.Id, d.CameraId, d.TrackId, d.Kind, d.DetectedAt })
      .SingleOrDefaultAsync(ct)
      ?? throw new RpcException(new Status(StatusCode.NotFound, "detection not found"));

    var embeddings = await GatherTrackProbeVectorsAsync(
      db, source.CameraId, source.TrackId, source.Kind, source.DetectedAt, ct);
    if (embeddings.Count > 0)
      return new ResolvedProbe(source.Kind, embeddings, source.DetectedAt, detectionId);

    // Live / recently persisted detections may have a crop but no stored vector yet (embedder
    // missing at write time, or store_embeddings was off). Re-embed the crop on demand.
    var fromCrop = await TryEmbedFromDetectionCropAsync(db, detectionId, source.Kind, source.DetectedAt, ct)
      ?? await TryEmbedNearestTrackCropAsync(db, source.CameraId, source.TrackId, source.Kind, source.DetectedAt, ct);
    if (fromCrop is not null)
      return fromCrop;

    throw new RpcException(new Status(StatusCode.FailedPrecondition, "probe has no embedding"));
  }

  private async Task<ResolvedProbe> ResolveArchiveProbeAsync(
    AnalyticsDbContext db, ArchiveTrackProbe probe, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(probe.CameraId))
      throw new RpcException(new Status(StatusCode.InvalidArgument, "camera_id must not be empty"));
    if (probe.HasKind && probe.Kind == DetectionKind.Unspecified)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "kind must not be unspecified"));

    var kind = probe.HasKind ? DetectionKinds.ToDbString(probe.Kind) : null;
    var ts = probe.Ts?.ToDateTime() ?? DateTime.UtcNow;
    if (kind is null)
      kind = (await FindNearestTrackEmbeddingAsync(db, probe.CameraId, probe.TrackId, kind: null, ts, ct))?.Kind;

    var embeddings = await GatherTrackProbeVectorsAsync(db, probe.CameraId, probe.TrackId, kind, ts, ct);
    if (embeddings.Count > 0 && kind is not null)
      return new ResolvedProbe(kind, embeddings, ts, null);

    var fromCrop = await TryEmbedNearestTrackCropAsync(db, probe.CameraId, probe.TrackId, kind, ts, ct);
    if (fromCrop is not null)
      return fromCrop;

    throw new RpcException(new Status(StatusCode.FailedPrecondition, "probe has no embedding"));
  }

  private async Task<List<float[]>> GatherTrackProbeVectorsAsync(
    AnalyticsDbContext db,
    string cameraId,
    long trackId,
    string? kind,
    DateTime anchor,
    CancellationToken ct)
  {
    var window = await LoadTrackWindowEmbeddingsAsync(db, cameraId, trackId, kind, anchor, ct);
    if (window.Count > 0)
      return window;

    var fallback = await FindNearestTrackEmbeddingAsync(db, cameraId, trackId, kind, anchor, ct);
    if (fallback is not null)
      return [fallback.Vector];

    if (kind is not null
        && recentEmbeddings?.TryGet(cameraId, trackId, kind, anchor, out var liveVector, out _) == true)
      return [liveVector];

    return [];
  }

  private static async Task<List<float[]>> LoadTrackWindowEmbeddingsAsync(
    AnalyticsDbContext db,
    string cameraId,
    long trackId,
    string? kind,
    DateTime anchor,
    CancellationToken ct)
  {
    var window = ProbeWindowForKind(kind);
    var windowFrom = anchor - window;
    var windowTo = anchor + window;
    var rows = await (
      from d in db.Detections
      join e in db.DetectionEmbeddings on d.Id equals e.DetectionId
      where d.CameraId == cameraId
            && d.TrackId == trackId
            && d.DetectedAt >= windowFrom
            && d.DetectedAt <= windowTo
            && (kind == null || d.Kind == kind)
            && (kind == null || e.Kind == kind)
      select new { d.DetectedAt, e.Dim, e.Embedding }
    ).ToListAsync(ct);

    var decoded = rows
      .OrderBy(r => Math.Abs((r.DetectedAt - anchor).Ticks))
      .Take(kind == "face" ? MaxFaceProbeExemplars * 2 : MaxProbeExemplars)
      .Select(r => EmbeddingCodec.DecodeNormalized(r.Embedding, r.Dim))
      .Where(v => v.Length > 0)
      .ToList();

    return kind == "face" ? SelectFaceProbeVectors(decoded) : decoded;
  }

  private static TimeSpan ProbeWindowForKind(string? kind)
    => kind == "face" ? FaceProbeWindow : ProbeExemplarWindow;

  /// <summary>
  /// Keep the nearest face vector and any later/earlier samples that still look like it.
  /// A person-track often grows a second face (or a false crop) within seconds; max-cosine
  /// against that mixed set matches whoever looks like either identity.
  /// </summary>
  public static List<float[]> SelectFaceProbeVectors(
    IReadOnlyList<float[]> nearestFirst, int max = MaxFaceProbeExemplars, float minSelf = FaceProbeSelfMin)
  {
    if (nearestFirst.Count == 0)
      return [];

    var nearest = nearestFirst[0];
    var selected = new List<float[]> { nearest };
    foreach (var vector in nearestFirst.Skip(1))
    {
      if (selected.Count >= max)
        break;
      if (vector.Length != nearest.Length)
        continue;
      if (EmbeddingCodec.Cosine(nearest, vector) >= minSelf)
        selected.Add(vector);
    }
    return selected;
  }

  private async Task<ResolvedProbe?> TryEmbedNearestTrackCropAsync(
    AnalyticsDbContext db,
    string cameraId,
    long trackId,
    string? kind,
    DateTime anchor,
    CancellationToken ct)
  {
    var q = db.Detections.Where(d =>
      d.CameraId == cameraId
      && d.TrackId == trackId
      && d.CropPath != null
      && d.StorageId != null
      && (kind == null || d.Kind == kind));

    var before = await q
      .Where(d => d.DetectedAt <= anchor)
      .OrderByDescending(d => d.DetectedAt)
      .Select(d => new { d.Id, d.Kind, d.DetectedAt })
      .FirstOrDefaultAsync(ct);
    var after = await q
      .Where(d => d.DetectedAt > anchor)
      .OrderBy(d => d.DetectedAt)
      .Select(d => new { d.Id, d.Kind, d.DetectedAt })
      .FirstOrDefaultAsync(ct);

    var chosen = before is null ? after
      : after is null ? before
      : (anchor - before.DetectedAt) <= (after.DetectedAt - anchor) ? before : after;
    if (chosen is null)
      return null;

    return await TryEmbedFromDetectionCropAsync(db, chosen.Id, chosen.Kind, chosen.DetectedAt, ct);
  }

  private async Task<ResolvedProbe?> TryEmbedFromDetectionCropAsync(
    AnalyticsDbContext db,
    long detectionId,
    string kind,
    DateTime detectedAt,
    CancellationToken ct)
  {
    if (workers is null || workers.Workers.Count == 0 || storageResolver is null)
      return null;

    var row = await db.Detections
      .Where(d => d.Id == detectionId)
      .Select(d => new { d.StorageId, d.CropPath, d.Kind })
      .SingleOrDefaultAsync(ct);
    if (row?.StorageId is null || row.CropPath is null)
      return null;

    var root = await storageResolver.GetRootPathAsync(row.StorageId, ct);
    if (root is null)
      return null;
    var fullPath = Path.Combine(root, row.CropPath);
    if (!File.Exists(fullPath))
      return null;

    var jpeg = await File.ReadAllBytesAsync(fullPath, ct);
    if (jpeg.Length == 0)
      return null;

    var workerKind = MapWorkerKind(DetectionKinds.Parse(row.Kind));
    if (workerKind == Analytics.DetectionKind.Unspecified)
      return null;

    try
    {
      var workerId = workers.Workers.Keys.First();
      var client = workers.ClientFor(workerId);
      // Crop is already the object — do not run detect-first on a tight face/plate crop.
      var workerResponse = await client.EmbedImageAsync(jpeg, workerKind, bbox: null, detectFirst: false, ct);
      if (!workerResponse.Success || workerResponse.Embedding.Count == 0)
        return null;
      if (!EmbeddingCodec.TryEncodeNormalized(workerResponse.Embedding, out var bytes, out var dim))
        return null;
      var embedding = EmbeddingCodec.DecodeNormalized(bytes, dim);
      if (embedding.Length == 0)
        return null;
      return new ResolvedProbe(kind, [embedding], detectedAt, detectionId);
    }
    catch (Exception)
    {
      return null;
    }
  }

  private async Task<ResolvedProbe> ResolveImageProbeAsync(ImageProbe probe, CancellationToken ct)
  {
    if (probe.Jpeg.IsEmpty)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "image probe jpeg is required"));
    if (workers is null || workers.Workers.Count == 0)
      throw new RpcException(new Status(StatusCode.Unavailable, "photo search unavailable"));

    var workerKind = MapWorkerKind(probe.Kind);
    if (workerKind == Analytics.DetectionKind.Unspecified)
      throw new RpcException(new Status(StatusCode.FailedPrecondition, "kind space unavailable"));

    var workerId = workers.Workers.Keys.First();
    var client = workers.ClientFor(workerId);
    var workerResponse = await client.EmbedImageAsync(
      probe.Jpeg.ToByteArray(),
      workerKind,
      probe.Bbox is not null
        ? new Analytics.BoundingBox
        {
          X = probe.Bbox.X,
          Y = probe.Bbox.Y,
          Width = probe.Bbox.Width,
          Height = probe.Bbox.Height,
        }
        : null,
      probe.DetectFirst,
      ct);

    if (!workerResponse.Success || workerResponse.Embedding.Count == 0)
      throw new RpcException(new Status(StatusCode.Unavailable, "photo search unavailable"));
    if (!EmbeddingCodec.TryEncodeNormalized(workerResponse.Embedding, out var bytes, out var dim))
      throw new RpcException(new Status(StatusCode.FailedPrecondition, "kind space unavailable"));

    var embedding = EmbeddingCodec.DecodeNormalized(bytes, dim);
    if (embedding.Length == 0)
      throw new RpcException(new Status(StatusCode.FailedPrecondition, "kind space unavailable"));
    return new ResolvedProbe(DetectionKinds.ToDbString(probe.Kind), [embedding], DateTime.UtcNow, null);
  }

  private static async Task<TrackProbeCandidate?> FindNearestTrackEmbeddingAsync(
    AnalyticsDbContext db,
    string cameraId,
    long trackId,
    string? kind,
    DateTime anchor,
    CancellationToken ct)
  {
    var baseQuery = from d in db.Detections
      join e in db.DetectionEmbeddings on d.Id equals e.DetectionId
      where d.CameraId == cameraId
            && d.TrackId == trackId
            && (kind == null || d.Kind == kind)
            && (kind == null || e.Kind == kind)
      select new CandidateProjection
      {
        Detection = d,
        Dim = e.Dim,
        Embedding = e.Embedding,
      };

    var before = await baseQuery
      .Where(x => x.Detection.DetectedAt <= anchor)
      .OrderByDescending(x => x.Detection.DetectedAt)
      .FirstOrDefaultAsync(ct);
    var after = await baseQuery
      .Where(x => x.Detection.DetectedAt > anchor)
      .OrderBy(x => x.Detection.DetectedAt)
      .FirstOrDefaultAsync(ct);

    var chosen = ChooseNearest(anchor, before, after);
    if (chosen is null)
      return null;

    var vector = EmbeddingCodec.DecodeNormalized(chosen.Embedding, chosen.Dim);
    if (vector.Length == 0)
      return null;

    return new TrackProbeCandidate(
      chosen.Detection.Id,
      chosen.Detection.Kind,
      chosen.Detection.DetectedAt,
      vector);
  }

  private static CandidateProjection? ChooseNearest(DateTime anchor, CandidateProjection? before, CandidateProjection? after)
  {
    if (before is null) return after;
    if (after is null) return before;
    var beforeDiff = anchor - before.Detection.DetectedAt;
    var afterDiff = after.Detection.DetectedAt - anchor;
    return beforeDiff <= afterDiff ? before : after;
  }

  private static float ResolveDefaultMinScore()
  {
    var raw = Environment.GetEnvironmentVariable("ANALYTICS_REID_MATCH_MIN_SCORE");
    if (!float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
      return 0.45f;
    return Math.Clamp(parsed, -1f, 1f);
  }

  private static Analytics.DetectionKind MapWorkerKind(DetectionKind kind) => kind switch
  {
    DetectionKind.Person => Analytics.DetectionKind.Person,
    DetectionKind.Face => Analytics.DetectionKind.Face,
    DetectionKind.Vehicle => Analytics.DetectionKind.Vehicle,
    DetectionKind.LicensePlate => Analytics.DetectionKind.LicensePlate,
    _ => Analytics.DetectionKind.Unspecified,
  };

  private static Detection ToProto(DbDetection row)
  {
    var detection = new Detection
    {
      Id = row.Id,
      CameraId = row.CameraId,
      TrackId = row.TrackId,
      Kind = DetectionKinds.Parse(row.Kind),
      Confidence = row.Confidence,
      DetectedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(row.DetectedAt, DateTimeKind.Utc)),
      EventId = row.EventId ?? string.Empty,
      EventUuid = row.EventUuid?.ToString() ?? string.Empty,
      HasCrop = row.CropPath != null,
    };
    if (row.PlateText is not null) detection.PlateText = row.PlateText;
    if (row.ObjectId is not null) detection.ObjectId = row.ObjectId;
    return detection;
  }

  private sealed class CandidateProjection
  {
    public required DbDetection Detection { get; init; }
    public required int Dim { get; init; }
    public required byte[] Embedding { get; init; }
  }
  private sealed record ResolvedProbe(string Kind, IReadOnlyList<float[]> Embeddings, DateTime DetectedAt, long? SourceDetectionId);
  private sealed record TrackProbeCandidate(long DetectionId, string Kind, DateTime DetectedAt, float[] Vector);

}
