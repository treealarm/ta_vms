using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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
      var rows = await _rightService.GetListByIdsAsync(request.Ids.ToList());

      return new MapProto.RightList
      {
        Rights =
        {
          rows.Values
            .SelectMany(list => list)
            .Select(r => new MapProto.Right
            {
              ObjectId = r.object_id ?? string.Empty,
              Subject = r.subject ?? string.Empty,
              Action = r.action ?? string.Empty,
            })
        }
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
