using System;
using System.Threading.Tasks;
using Grpc.Core;
using VmsMap.Domain.Arming;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The settings of a logical device object, addressed by the source a device card knows: the mode
  // its arming is decided with, and the contact state that raises an alarm. The operator's switch
  // keeps its own facade; this is the administrator's card.
  public partial class MapServiceImpl
  {
    public override async Task<MapProto.GetDeviceSettingsResponse> GetDeviceSettings(
      MapProto.GetDeviceSettingsRequest request, ServerCallContext context)
    {
      var view = await _deviceSettings.GetAsync(
        ToKey(request.Key), context.CancellationToken);

      return view is null
        ? new MapProto.GetDeviceSettingsResponse { Found = false }
        : new MapProto.GetDeviceSettingsResponse { Found = true, Settings = ToSettings(view) };
    }

    public override async Task<MapProto.GetDeviceSettingsResponse> SetDeviceSettings(
      MapProto.SetDeviceSettingsRequest request, ServerCallContext context)
    {
      var caller = ToCaller(request.Caller);
      try
      {
        var view = await _deviceSettings.SetAsync(
          ToKey(request.Key), request.ArmMode, EmptyToNull(request.TriggerOn), request.AlarmEnabled,
          caller.Username ?? string.Empty, context.CancellationToken);

        return view is null
          ? new MapProto.GetDeviceSettingsResponse { Found = false }
          : new MapProto.GetDeviceSettingsResponse { Found = true, Settings = ToSettings(view) };
      }
      catch (ArmingException ex)
      {
        throw new RpcException(new Status(StatusOf(ex.Refusal), ex.Message));
      }
    }

    public override async Task<MapProto.GetNodeArmingResponse> SetDeviceArming(
      MapProto.SetDeviceArmingRequest request, ServerCallContext context)
    {
      var caller = ToCaller(request.Caller);
      try
      {
        var view = await _deviceSettings.SetArmingAsync(
          ToKey(request.Key), request.Mode, caller.Username ?? string.Empty,
          context.CancellationToken);

        return view is null
          ? new MapProto.GetNodeArmingResponse { Found = false }
          : ToArming(view);
      }
      catch (ArmingException ex)
      {
        throw new RpcException(new Status(StatusOf(ex.Refusal), ex.Message));
      }
    }

    private static DeviceSettingsKey ToKey(MapProto.DeviceSettingsKey? key) => key is null
      ? new DeviceSettingsKey(string.Empty, string.Empty, string.Empty, string.Empty)
      : new DeviceSettingsKey(key.SourceKind, key.SourceId, key.PartKind, key.PartToken);

    private static MapProto.DeviceSettingsView ToSettings(DeviceSettingsView view)
    {
      var settings = new MapProto.DeviceSettingsView
      {
        ObjectId = view.ObjectId,
        Key = new MapProto.DeviceSettingsKey
        {
          SourceKind = view.Key.SourceKind,
          SourceId = view.Key.SourceId,
          PartKind = view.Key.PartKind,
          PartToken = view.Key.PartToken,
        },
        ArmMode = view.Mode,
        TriggerOn = view.TriggerOn ?? string.Empty,
        AlarmEnabled = view.AlarmEnabled,
        Configured = view.Configured,
      };
      settings.ArmModes.AddRange(DeviceSettingsView.ArmModes);
      settings.TriggerModes.AddRange(DeviceSettingsView.TriggerModes);

      if (view.Effective is { } arming)
      {
        settings.Arming = new MapProto.NodeArming
        {
          ObjectId = arming.ObjectId,
          Armed = arming.Armed,
          Source = arming.Source,
          SourceObjectId = arming.SourceObjectId,
        };
      }

      return settings;
    }

    private static StatusCode StatusOf(ArmingRefusal refusal) => refusal switch
    {
      ArmingRefusal.NotFound => StatusCode.NotFound,
      ArmingRefusal.InvalidRequest => StatusCode.InvalidArgument,
      _ => StatusCode.FailedPrecondition,
    };

    private static string? EmptyToNull(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value;
  }
}
