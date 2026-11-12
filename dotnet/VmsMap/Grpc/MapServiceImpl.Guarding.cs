using System;
using System.Threading.Tasks;
using Grpc.Core;
using VmsMap.Db;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The operator's guarding panel. The list is a read of the tree plus its devices, so it belongs
  // with the other reads; it lives here rather than in Reads because the selection, the arming and
  // the rights of a whole page are one concern of their own (GuardSensorQuery).
  public partial class MapServiceImpl
  {
    public override async Task<MapProto.ListGuardSensorsResponse> ListGuardSensors(
      MapProto.ListGuardSensorsRequest request, ServerCallContext context)
    {
      var ct = context.CancellationToken;

      if (!string.IsNullOrWhiteSpace(request.SpaceId)
          && Utils.ConvertObjectIdToGuid(request.SpaceId) is null)
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "space_id must be a UUID"));
      }

      var after = request.After;
      if (after is not null
          && (string.IsNullOrWhiteSpace(after.AfterName) || Utils.ConvertObjectIdToGuid(after.AfterId) is null))
      {
        throw new RpcException(new Status(
          StatusCode.InvalidArgument, "after_name and after_id must both be set"));
      }

      var page = await _guardSensors.ListAsync(
        request.SpaceId,
        request.PageSize,
        after?.AfterName,
        after?.AfterId,
        ToCaller(request.Caller),
        ct);

      var response = new MapProto.ListGuardSensorsResponse { HasMore = page.HasMore };
      foreach (var row in page.Rows)
      {
        response.Sensors.Add(new MapProto.GuardSensor
        {
          SourceKind = row.SourceKind,
          SourceId = row.SourceId,
          PartKind = row.PartKind,
          PartToken = row.PartToken,
          ObjectId = row.ObjectId,
          Name = row.Name,
          CameraName = row.CameraName,
          SpaceId = row.SpaceId,
          SpaceName = row.SpaceName,
          ArmMode = row.ArmMode,
          AlarmEnabled = row.AlarmEnabled,
          Configured = row.Configured,
          Armed = row.Armed,
          ArmingSource = row.ArmingSource,
          CanControl = row.CanControl,
        });
      }

      if (page.HasMore)
      {
        response.Next = new MapProto.GuardSensorCursor
        {
          AfterName = page.NextName,
          AfterId = page.NextId,
        };
      }

      return response;
    }
  }
}
