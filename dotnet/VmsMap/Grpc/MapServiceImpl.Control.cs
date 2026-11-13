using System;
using System.Threading.Tasks;
using Grpc.Core;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Control;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The operator's control panel: the list of objects an operator acts on, and the same row for
  // one node. It lives beside the other reads rather than inside them because the selection, the
  // rights, the arming and the live state of a whole page are one concern of their own
  // (ControlObjectQuery).
  public partial class MapServiceImpl
  {
    public override async Task<MapProto.ListControlObjectsResponse> ListControlObjects(
      MapProto.ListControlObjectsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      if (!string.IsNullOrWhiteSpace(request.SpaceId)
          && Utils.ConvertObjectIdToGuid(request.SpaceId) is null)
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "space_id must be a UUID"));
      }

      if (!string.IsNullOrWhiteSpace(request.CameraId)
          && Utils.ConvertObjectIdToGuid(request.CameraId) is null)
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "camera_id must be a UUID"));
      }

      try
      {
        ControlObjectQuery.ParseKind(request.Kind);
      }
      catch (ArgumentException ex)
      {
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
      }

      var after = request.After;
      if (after is not null
          && (string.IsNullOrWhiteSpace(after.AfterName) || Utils.ConvertObjectIdToGuid(after.AfterId) is null))
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "after_name and after_id must both be set"));
      }

      var page = await _controlObjects.ListAsync(
        request.SpaceId,
        request.Kind,
        request.CameraId,
        request.PageSize,
        after?.AfterName,
        after?.AfterId,
        ToCaller(request.Caller),
        ct);

      var response = new MapProto.ListControlObjectsResponse { HasMore = page.HasMore };
      foreach (var row in page.Rows)
        response.Objects.Add(ToProto(row));

      if (page.HasMore)
      {
        response.Next = new MapProto.ControlObjectCursor
        {
          AfterName = page.NextName,
          AfterId = page.NextId,
        };
      }

      return response;
    }

    public override async Task<MapProto.GetControlObjectResponse> GetControlObject(
      MapProto.GetControlObjectRequest request, ServerCallContext context)
    {
      if (Utils.ConvertObjectIdToGuid(request.ObjectId) is null)
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "object_id must be a UUID"));
      }

      var row = await _controlObjects.FindAsync(
        request.ObjectId, ToCaller(request.Caller), context.CancellationToken);

      return row is null
        ? new MapProto.GetControlObjectResponse { Found = false }
        : new MapProto.GetControlObjectResponse { Found = true, Object = ToProto(row) };
    }

    public override async Task<MapProto.GetNodeArmingResponse> SetNodeArming(
      MapProto.SetNodeArmingRequest request, ServerCallContext context)
    {
      if (Utils.ConvertObjectIdToGuid(request.ObjectId) is null)
        throw new RpcException(new Status(StatusCode.InvalidArgument, "object_id must be a UUID"));

      var caller = ToCaller(request.Caller);

      try
      {
        var view = await _nodeArming.SetAsync(
          request.ObjectId, request.Mode, caller.Username ?? string.Empty,
          context.CancellationToken);

        return ToArming(view!);
      }
      catch (VmsMap.Domain.Arming.ArmingException ex)
      {
        throw new RpcException(new Status(ex.Refusal switch
        {
          VmsMap.Domain.Arming.ArmingRefusal.NotFound => StatusCode.NotFound,
          VmsMap.Domain.Arming.ArmingRefusal.InvalidRequest => StatusCode.InvalidArgument,
          _ => StatusCode.FailedPrecondition,
        }, ex.Message));
      }
    }

    private static MapProto.GetNodeArmingResponse ToArming(VmsMap.Domain.Arming.NodeArmingView view)
    {
      var response = new MapProto.GetNodeArmingResponse
      {
        Found = view.Found,
        Armable = view.Armable,
        Configured = view.Configured,
        Mode = view.Mode,
      };
      response.Modes.AddRange(view.Modes);

      if (view.Arming is { } arming)
      {
        response.Arming = new MapProto.NodeArming
        {
          ObjectId = arming.ObjectId,
          Armed = arming.Armed,
          Source = arming.Source,
          SourceObjectId = arming.SourceObjectId,
        };
      }

      return response;
    }

    private static MapProto.ControlObject ToProto(ControlObjectRow row) => new()
    {
      ObjectId = row.ObjectId,
      Kind = row.Kind,
      SourceKind = row.SourceKind,
      SourceId = row.SourceId,
      PartKind = row.PartKind,
      PartToken = row.PartToken,
      Name = row.Name,
      CameraName = row.CameraName,
      SpaceId = row.SpaceId,
      SpaceName = row.SpaceName,
      State = row.State,
      ArmMode = row.ArmMode,
      AlarmEnabled = row.AlarmEnabled,
      Configured = row.Configured,
      HasArming = row.HasArming,
      Armed = row.Armed,
      ArmingSource = row.ArmingSource,
      CanControl = row.CanControl,
    };
  }
}
