using System.Text.Json.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Cfg;

namespace VmsCfg
{
  /// Maps & plans: spaces, plan images, camera placements, landmarks and PTZ observations.
  public partial class CfgServiceImpl
  {
    // Announces that a space, its plan or one of its landmarks changed. Its own topic rather than
    // server-config-changed with an empty camera list: that means "everything on this node changed"
    // and would make every media server, VmsFs and VmsOnvif reconcile for an edit that concerns none
    // of them. Same shape of event as storage-config-changed — one per aggregate.
    // Payload: {"space_id": "..."}; addressing only, subscribers re-read what they need.
    private const string SpaceConfigChangedTopic = "space-config-changed";

    /// Largest plan image accepted. A floor plan worth looking at is a few megabytes; the limit is
    /// there so one upload cannot hold a large buffer in every hop it passes.
    internal const int MaxPlanImageBytes = 20 * 1024 * 1024;

    /// Size of one streamed piece of a plan image, well under the 4 MiB a message may carry
    /// through Dapr service invocation.
    internal const int PlanImageChunkBytes = 256 * 1024;

    private sealed record SpaceConfigChanged([property: JsonPropertyName("space_id")] string SpaceId);

    private void NotifySpaceChanged(Guid spaceId)
    {
      _ = Task.Run(async () =>
      {
        try
        {
          await daprClient.PublishEventAsync(
            _pubSubName, SpaceConfigChangedTopic, new SpaceConfigChanged(spaceId.ToString()));
        }
        catch (Exception ex)
        {
          logger.LogWarning(ex, "Space change notification failed for '{SpaceId}'", spaceId);
        }
      });
    }

    private static RpcException Invalid(SpatialConfigException ex) =>
      new(new Status(StatusCode.InvalidArgument, ex.Message));

    private static OperationStatus Failed(string message) => new() { Success = false, Message = message };

    private static OperationStatus Ok() => new() { Success = true };

    // ── Spaces ──────────────────────────────────────────────────────────────

    public override Task<ListSpacesResponse> ListSpaces(Empty request, ServerCallContext context) =>
      spatialDb.ListSpacesAsync(context.CancellationToken);

    public override async Task<GetSpaceResponse> GetSpace(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));
      var space = await spatialDb.GetSpaceAsync(spaceId, context.CancellationToken);
      return space is null ? new GetSpaceResponse { Found = false } : new GetSpaceResponse { Found = true, Space = space };
    }

    public override async Task<Space> UpsertSpace(Space request, ServerCallContext context)
    {
      try
      {
        var space = await spatialDb.UpsertSpaceAsync(request, context.CancellationToken);
        NotifySpaceChanged(Guid.Parse(space.Id));
        return space;
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveSpace(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");

      var ct = context.CancellationToken;
      // Read before the cascade: afterwards there is nothing left to say which cameras lost a
      // placement.
      var placed = await spatialDb.ListPlacedCamerasAsync(await spatialDb.SubtreeAsync(spaceId, ct), ct);
      if (!await spatialDb.RemoveSpaceAsync(spaceId, ct))
        return Failed($"Space '{request.SpaceId}' does not exist");

      NotifySpaceChanged(spaceId);
      foreach (var cameraId in placed)
        InvalidateFsCache(cameraId);
      return Ok();
    }

    // ── Plan images ─────────────────────────────────────────────────────────

    public override async Task<Space> UploadSpacePlan(
      IAsyncStreamReader<SpacePlanUploadChunk> requestStream, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (!await requestStream.MoveNext(ct) || requestStream.Current.PartCase != SpacePlanUploadChunk.PartOneofCase.Header)
        throw new RpcException(new Status(StatusCode.InvalidArgument, "The first message must be the header"));

      var header = requestStream.Current.Header;
      if (!PlanImageFormats.IsAllowed(header.Mime))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "A plan must be a PNG, JPEG or WebP image"));

      using var buffer = new MemoryStream();
      while (await requestStream.MoveNext(ct))
      {
        var chunk = requestStream.Current;
        if (chunk.PartCase != SpacePlanUploadChunk.PartOneofCase.Data)
          throw new RpcException(new Status(StatusCode.InvalidArgument, "Only the first message may be a header"));
        if (buffer.Length + chunk.Data.Length > MaxPlanImageBytes)
          throw new RpcException(new Status(StatusCode.ResourceExhausted,
            $"A plan image may not exceed {MaxPlanImageBytes / (1024 * 1024)} MiB"));
        chunk.Data.WriteTo(buffer);
      }

      var image = buffer.ToArray();
      if (!PlanImageFormats.Matches(header.Mime, image))
        throw new RpcException(new Status(StatusCode.InvalidArgument, $"The file is not what it claims to be ({header.Mime})"));

      try
      {
        var space = await spatialDb.SavePlanAsync(header, image, ct);
        NotifySpaceChanged(Guid.Parse(space.Id));
        logger.LogInformation("Plan uploaded for space '{SpaceId}': {Bytes} bytes", space.Id, image.Length);
        return space;
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task GetSpacePlanImage(
      GetSpacePlanImageRequest request, IServerStreamWriter<SpacePlanImageChunk> responseStream,
      ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      var plan = await spatialDb.GetPlanImageAsync(spaceId, request.IfNoneMatch, ct)
        ?? throw new RpcException(new Status(StatusCode.NotFound, "This space has no plan"));

      var notModified = plan.Image.Length == 0;
      await responseStream.WriteAsync(new SpacePlanImageChunk
      {
        Header = new SpacePlanImageHeader { Mime = plan.Mime, Etag = plan.Etag, NotModified = notModified },
      }, ct);

      for (var offset = 0; offset < plan.Image.Length; offset += PlanImageChunkBytes)
      {
        var length = Math.Min(PlanImageChunkBytes, plan.Image.Length - offset);
        await responseStream.WriteAsync(new SpacePlanImageChunk
        {
          Data = ByteString.CopyFrom(plan.Image, offset, length),
        }, ct);
      }
    }

    public override async Task<Space> SetSpacePlanCorners(SetSpacePlanCornersRequest request, ServerCallContext context)
    {
      try
      {
        var spaceId = SpatialDb.ParseId(request.SpaceId, "space");
        var space = await spatialDb.SetPlanCornersAsync(spaceId, request.Corners, context.CancellationToken);
        NotifySpaceChanged(spaceId);
        return space;
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveSpacePlan(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");

      if (!await spatialDb.RemovePlanAsync(spaceId, context.CancellationToken))
        return Failed("This space has no plan");

      NotifySpaceChanged(spaceId);
      return Ok();
    }

    // ── Camera placements ───────────────────────────────────────────────────

    public override async Task<ListCameraPlacementsResponse> ListCameraPlacements(Empty request, ServerCallContext context)
    {
      var response = new ListCameraPlacementsResponse();
      response.Placements.AddRange(await spatialDb.ListPlacementsAsync(context.CancellationToken));
      return response;
    }

    public override async Task<GetReferencePlacementResponse> GetReferencePlacement(SourceRequest request, ServerCallContext context)
    {
      var found = await spatialDb.GetReferencePlacementAsync(request.SourceId, context.CancellationToken);
      return found is null
        ? new GetReferencePlacementResponse { Found = false }
        : new GetReferencePlacementResponse { Found = true, Placement = found.Value.Placement, Space = found.Value.Space };
    }

    public override async Task<OperationStatus> UpsertCameraPlacement(CameraPlacement request, ServerCallContext context)
    {
      try
      {
        await spatialDb.UpsertPlacementAsync(request, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }

      InvalidateFsCache(request.CameraId);
      return Ok();
    }

    public override async Task<OperationStatus> RemoveCameraPlacement(CameraPlacementRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");
      if (!await spatialDb.RemovePlacementAsync(request.CameraId, spaceId, context.CancellationToken))
        return Failed("The camera is not drawn in this space");

      InvalidateFsCache(request.CameraId);
      return Ok();
    }

    // ── Landmarks ───────────────────────────────────────────────────────────

    public override async Task<ListLandmarksResponse> ListLandmarks(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      var response = new ListLandmarksResponse();
      response.Landmarks.AddRange(await spatialDb.ListLandmarksAsync(spaceId, context.CancellationToken));
      return response;
    }

    // A landmark is announced as a space change, not as a change to each camera that observed it:
    // the aiming service drops every spatial model on that event anyway, and nothing else cares.
    public override async Task<Landmark> UpsertLandmark(Landmark request, ServerCallContext context)
    {
      try
      {
        var landmark = await spatialDb.UpsertLandmarkAsync(request, context.CancellationToken);
        NotifySpaceChanged(Guid.Parse(landmark.SpaceId));
        return landmark;
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveLandmark(LandmarkRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.LandmarkId, out var landmarkId))
        return Failed("landmark_id is not a valid id");

      var ct = context.CancellationToken;
      var spaceId = await spatialDb.SpaceIdOfLandmarkAsync(landmarkId, ct);
      if (spaceId is null || !await spatialDb.RemoveLandmarkAsync(landmarkId, ct))
        return Failed($"Landmark '{request.LandmarkId}' does not exist");

      NotifySpaceChanged(spaceId.Value);
      return Ok();
    }

    // ── PTZ observations ────────────────────────────────────────────────────

    public override Task<PtzGeoObservations> GetPtzGeoObservations(SourceRequest request, ServerCallContext context) =>
      spatialDb.GetObservationsAsync(request.SourceId, context.CancellationToken);

    public override async Task<OperationStatus> SetPtzGeoObservations(PtzGeoObservations request, ServerCallContext context)
    {
      try
      {
        await spatialDb.SetObservationsAsync(request, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }

      InvalidateFsCache(request.CameraId);
      return Ok();
    }

    // ── Ground observations ─────────────────────────────────────────────────

    public override Task<GroundObservations> GetGroundObservations(SourceRequest request, ServerCallContext context) =>
      spatialDb.GetGroundObservationsAsync(request.SourceId, context.CancellationToken);

    public override async Task<OperationStatus> SetGroundObservations(GroundObservations request, ServerCallContext context)
    {
      try
      {
        await spatialDb.SetGroundObservationsAsync(request, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }

      InvalidateFsCache(request.CameraId);
      return Ok();
    }

    // ── Spatial models ──────────────────────────────────────────────────────

    public override Task<CameraSpatialModelResponse> GetCameraSpatialModel(SourceRequest request, ServerCallContext context) =>
      spatialDb.GetCameraSpatialModelAsync(request.SourceId, context.CancellationToken);

    public override async Task<ListCameraSpatialModelsResponse> ListCameraSpatialModels(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));
      try
      {
        return await spatialDb.ListCameraSpatialModelsAsync(spaceId, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }
  }

  /// Image types a plan may be, checked by content as well as by the declared type: the bytes are
  /// served back to browsers under that type.
  internal static class PlanImageFormats
  {
    private static readonly Dictionary<string, Func<byte[], bool>> Signatures = new(StringComparer.OrdinalIgnoreCase)
    {
      ["image/png"]  = b => b.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
      ["image/jpeg"] = b => b.AsSpan().StartsWith((byte[])[0xFF, 0xD8, 0xFF]),
      ["image/webp"] = b => b.Length >= 12
                            && b.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                            && b.AsSpan(8, 4).SequenceEqual("WEBP"u8),
    };

    public static bool IsAllowed(string mime) => Signatures.ContainsKey(mime);

    public static bool Matches(string mime, byte[] image) =>
      Signatures.TryGetValue(mime, out var check) && check(image);
  }
}
