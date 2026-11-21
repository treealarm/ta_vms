using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using VmsMap.Adapter;
using VmsMap.Db;

namespace VmsMap.Domain.Arming
{
  /// <summary>
  /// The side effects a device arming write owes: a camera's flag in VmsCfg and the sensors' alarm
  /// re-check both follow from the mode. Every write path calls this one helper — the node facade
  /// and the card facade alike — so a third caller cannot forget them. A failure is logged and does
  /// not undo the write: the next snapshot, event or start repairs it.
  /// </summary>
  public sealed class DeviceArmingDerivation(
    CameraArmingReconciler cameras,
    SensorAlarmService sensors,
    ILogger<DeviceArmingDerivation> logger)
  {
    public async Task DeriveAsync(string partKind, string cameraId, CancellationToken ct)
    {
      try
      {
        if (string.Equals(partKind, DeviceObjectId.DevicePart, StringComparison.Ordinal))
          await cameras.ReconcileAsync(cameraId, ct);
        else
          await sensors.ReconcileCameraAsync(cameraId, ct);
      }
      catch (Exception ex) when (ex is not OperationCanceledException)
      {
        logger.LogWarning(
          ex, "Re-deriving the arming of camera {CameraId} failed", cameraId);
      }
    }
  }
}
