using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Map;
using VmsMap.Db;

namespace VmsMap.Grpc
{
  // Maps & plans: spaces, plan images, camera placements, landmarks, PTZ and ground observations.
  // A thin layer like the rest of MapService: parse, call SpatialDb, turn a configuration error
  // into the status the administrator sees. SpatialDb publishes map-config-changed itself, after
  // the write commits.
  public partial class MapServiceImpl
  {
    /// Largest plan image accepted. A floor plan worth looking at is a few megabytes; the limit is
    /// there so one upload cannot hold a large buffer in every hop it passes.
    internal const int MaxPlanImageBytes = 20 * 1024 * 1024;

    /// Size of one streamed piece of a plan image, well under the 4 MiB a message may carry
    /// through Dapr service invocation.
    internal const int PlanImageChunkBytes = 256 * 1024;

    private static RpcException Invalid(SpatialConfigException ex) =>
      new(new Status(StatusCode.InvalidArgument, ex.Message));

    private static OperationStatus Failed(string message) => new() { Success = false, Message = message };

    // ── Spaces ────────────────────────────────────────────────────────────────

    public override Task<ListSpacesResponse> ListSpaces(Empty request, ServerCallContext context) =>
      _spatialDb.ListSpacesAsync(context.CancellationToken);

    public override async Task<GetSpaceResponse> GetSpace(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      var space = await _spatialDb.GetSpaceAsync(spaceId, context.CancellationToken);
      return space is null
        ? new GetSpaceResponse { Found = false }
        : new GetSpaceResponse { Found = true, Space = space };
    }

    public override async Task<Space> UpsertSpace(Space request, ServerCallContext context)
    {
      try
      {
        return await _spatialDb.UpsertSpaceAsync(request, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveSpace(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");

      return await _spatialDb.RemoveSpaceAsync(spaceId, context.CancellationToken)
        ? Ok()
        : Failed($"Space '{request.SpaceId}' does not exist");
    }

    // ── Plan images ──────────────────────────────────────────────────────────

    public override async Task<Space> UploadSpacePlan(
      IAsyncStreamReader<SpacePlanUploadChunk> requestStream, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (!await requestStream.MoveNext(ct) ||
          requestStream.Current.PartCase != SpacePlanUploadChunk.PartOneofCase.Header)
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
        throw new RpcException(new Status(StatusCode.InvalidArgument,
          $"The file is not what it claims to be ({header.Mime})"));

      try
      {
        return await _spatialDb.SavePlanAsync(header, image, ct);
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

      var plan = await _spatialDb.GetPlanImageAsync(spaceId, request.IfNoneMatch, ct)
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

    public override async Task<Space> SetSpacePlanCorners(
      SetSpacePlanCornersRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      try
      {
        return await _spatialDb.SetPlanCornersAsync(spaceId, request.Corners, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveSpacePlan(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");

      return await _spatialDb.RemovePlanAsync(spaceId, context.CancellationToken)
        ? Ok()
        : Failed("This space has no plan");
    }

    // ── Camera placements ────────────────────────────────────────────────────

    public override async Task<ListCameraPlacementsResponse> ListCameraPlacements(
      Empty request, ServerCallContext context)
    {
      var response = new ListCameraPlacementsResponse();
      response.Placements.AddRange(await _spatialDb.ListPlacementsAsync(context.CancellationToken));
      return response;
    }

    public override async Task<GetReferencePlacementResponse> GetReferencePlacement(
      CameraRequest request, ServerCallContext context)
    {
      var found = await _spatialDb.GetReferencePlacementAsync(request.CameraId, context.CancellationToken);
      return found is null
        ? new GetReferencePlacementResponse { Found = false }
        : new GetReferencePlacementResponse
        {
          Found = true,
          Placement = found.Value.Placement,
          Space = found.Value.Space,
        };
    }

    public override async Task<OperationStatus> UpsertCameraPlacement(
      CameraPlacement request, ServerCallContext context)
    {
      try
      {
        await _spatialDb.UpsertPlacementAsync(request, context.CancellationToken);
        return Ok();
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }
    }

    public override async Task<OperationStatus> RemoveCameraPlacement(
      CameraPlacementRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        return Failed("space_id is not a valid id");

      return await _spatialDb.RemovePlacementAsync(request.CameraId, spaceId, context.CancellationToken)
        ? Ok()
        : Failed("The camera is not drawn in this space");
    }

    // ── Landmarks ────────────────────────────────────────────────────────────

    public override async Task<ListLandmarksResponse> ListLandmarks(SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      var response = new ListLandmarksResponse();
      response.Landmarks.AddRange(await _spatialDb.ListLandmarksAsync(spaceId, context.CancellationToken));
      return response;
    }

    public override async Task<Landmark> UpsertLandmark(Landmark request, ServerCallContext context)
    {
      try
      {
        return await _spatialDb.UpsertLandmarkAsync(request, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }

    public override async Task<OperationStatus> RemoveLandmark(LandmarkRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.LandmarkId, out var landmarkId))
        return Failed("landmark_id is not a valid id");

      return await _spatialDb.RemoveLandmarkAsync(landmarkId, context.CancellationToken)
        ? Ok()
        : Failed($"Landmark '{request.LandmarkId}' does not exist");
    }

    // ── PTZ and ground observations ──────────────────────────────────────────

    public override Task<PtzGeoObservations> GetPtzGeoObservations(
      CameraRequest request, ServerCallContext context) =>
      _spatialDb.GetObservationsAsync(request.CameraId, context.CancellationToken);

    public override async Task<OperationStatus> SetPtzGeoObservations(
      PtzGeoObservations request, ServerCallContext context)
    {
      try
      {
        await _spatialDb.SetObservationsAsync(request, context.CancellationToken);
        return Ok();
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }
    }

    public override Task<GroundObservations> GetGroundObservations(
      CameraRequest request, ServerCallContext context) =>
      _spatialDb.GetGroundObservationsAsync(request.CameraId, context.CancellationToken);

    public override async Task<OperationStatus> SetGroundObservations(
      GroundObservations request, ServerCallContext context)
    {
      try
      {
        await _spatialDb.SetGroundObservationsAsync(request, context.CancellationToken);
        return Ok();
      }
      catch (SpatialConfigException ex) { return Failed(ex.Message); }
    }

    // ── Spatial models ───────────────────────────────────────────────────────

    public override Task<CameraSpatialModelResponse> GetCameraSpatialModel(
      CameraRequest request, ServerCallContext context) =>
      _spatialDb.GetCameraSpatialModelAsync(request.CameraId, context.CancellationToken);

    public override async Task<ListCameraSpatialModelsResponse> ListCameraSpatialModels(
      SpaceRequest request, ServerCallContext context)
    {
      if (!Guid.TryParse(request.SpaceId, out var spaceId))
        throw new RpcException(new Status(StatusCode.InvalidArgument, "space_id is not a valid id"));

      try
      {
        return await _spatialDb.ListCameraSpatialModelsAsync(spaceId, context.CancellationToken);
      }
      catch (SpatialConfigException ex) { throw Invalid(ex); }
    }
  }

  /// Image types a plan may be, checked by content as well as by the declared type: the bytes are
  /// served back to browsers under that type.
  internal static class PlanImageFormats
  {
    private static readonly Dictionary<string, Func<byte[], bool>> Signatures =
      new(StringComparer.OrdinalIgnoreCase)
      {
        ["image/png"] = b => b.AsSpan().StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
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
