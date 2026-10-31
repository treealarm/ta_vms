using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The rights half of MapService: the stored rows, the effective set for one caller, the action
  // dictionary and the open/enforced switch. The write RPCs live in MapServiceImpl.cs, next to the
  // other writes, and bump rights-version there.
  public partial class MapServiceImpl
  {
    internal static CallerContext ToCaller(MapProto.Caller? caller) =>
      caller is null
        ? CallerContext.Anonymous
        : new CallerContext(caller.Username, caller.Roles.ToList(), caller.Unrestricted);

    public override async Task<MapProto.RightList> GetRights(
      MapProto.ObjectIds request, ServerCallContext context)
    {
      // No ids is the administrator's whole matrix; the legacy permissions page needs it to
      // rebuild its per-user view.
      var rows = request.Ids.Count == 0
        ? await _rightService.GetAllAsync()
        : (await _rightService.GetListByIdsAsync(request.Ids.ToList()))
            .Values.SelectMany(list => list).ToList();

      return new MapProto.RightList
      {
        Rights =
        {
          rows.Select(r => new MapProto.Right
          {
            ObjectId = r.object_id ?? string.Empty,
            Subject = r.subject ?? string.Empty,
            Action = r.action ?? string.Empty,
          })
        }
      };
    }

    public override async Task<MapProto.GetCameraNodeResponse> GetCameraNode(
      MapProto.CameraRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;
      if (string.IsNullOrWhiteSpace(request.CameraId))
        return new MapProto.GetCameraNodeResponse { Found = false };

      // camera_node is the canonical row; a database written before 0004 may only have the
      // reference placement, so fall back to it.
      var id = await _db.CameraNodes.AsNoTracking()
        .Where(n => n.camera_id == request.CameraId)
        .Select(n => (Guid?)n.object_id)
        .FirstOrDefaultAsync(ct);

      id ??= await _db.CameraPlacements.AsNoTracking()
        .Where(p => p.camera_id == request.CameraId && p.is_reference)
        .Select(p => (Guid?)p.object_id)
        .FirstOrDefaultAsync(ct);

      if (id is null)
        return new MapProto.GetCameraNodeResponse { Found = false };

      var marker = await _mapService.GetAsync(Utils.ConvertGuidToObjectId(id.Value));
      if (marker is null)
        return new MapProto.GetCameraNodeResponse { Found = false };

      var top = await _mapService.GetTopChildren([marker.id!]);
      return new MapProto.GetCameraNodeResponse
      {
        Found = true,
        Marker = ToMarker(
          marker, top.ContainsKey(marker.id!), request.CameraId, NodeKind.Camera),
      };
    }

    public override async Task<MapProto.EffectiveRightsResponse> GetEffectiveRights(
      MapProto.EffectiveRightsRequest request, ServerCallContext context)
    {
      var caller = ToCaller(request.Caller);
      var ct = context.CancellationToken;

      var mode = await _rightsService.GetModeAsync(ct);
      var nodes = caller.IsAdmin
        ? new List<NodeRightsDTO>()
        : await _rightsService.GetEffectiveAsync(caller, ct);

      return new MapProto.EffectiveRightsResponse
      {
        Mode = ToProto(mode),
        Admin = caller.IsAdmin,
        Nodes =
        {
          nodes.Select(n => new MapProto.NodeRights
          {
            ObjectId = n.object_id,
            CameraId = n.camera_id ?? string.Empty,
            Actions = { n.actions },
          })
        }
      };
    }

    public override Task<MapProto.RightActionTable> GetRightActions(
      Empty request, ServerCallContext context)
    {
      var table = new MapProto.RightActionTable();
      foreach (var kind in System.Enum.GetValues<NodeKind>())
      {
        table.Entries.Add(new MapProto.RightActionEntry
        {
          NodeKind = RightActions.Name(kind),
          Actions = { RightActions.For(kind) },
        });
      }

      return Task.FromResult(table);
    }

    public override async Task<MapProto.RightsModeResponse> GetRightsMode(
      Empty request, ServerCallContext context) =>
      new() { Mode = ToProto(await _rightsService.GetModeAsync(context.CancellationToken)) };

    public override async Task<MapProto.OperationStatus> SetRightsMode(
      MapProto.SetRightsModeRequest request, ServerCallContext context)
    {
      await _rightsService.SetModeAsync(FromProto(request.Mode), context.CancellationToken);
      await _rightsVersionNotifier.BumpAsync(context.CancellationToken);
      return Ok();
    }

    private static MapProto.RightsMode ToProto(RightsMode mode) =>
      mode == RightsMode.Open ? MapProto.RightsMode.Open : MapProto.RightsMode.Enforced;

    private static RightsMode FromProto(MapProto.RightsMode mode) =>
      mode == MapProto.RightsMode.Open ? RightsMode.Open : RightsMode.Enforced;
  }
}
