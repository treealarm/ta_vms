using System;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

/// Camera ids are UUIDs now (the map stores them as uuid columns). The tests keep them readable
/// under names: these two stand for whatever camera a test happens to need.
internal static class TestCameras
{
  public const string One = "11111111-1111-1111-1111-111111111111";
  public const string Two = "22222222-2222-2222-2222-222222222222";

  /// The registry row that makes `objectId` the camera's canonical node.
  public static DBDeviceNode DeviceNode(string cameraId, Guid objectId) => new()
  {
    object_id = objectId,
    source_kind = DeviceObjectId.CameraSource,
    source_id = cameraId,
    part_kind = DeviceObjectId.DevicePart,
    part_token = string.Empty,
  };
}
