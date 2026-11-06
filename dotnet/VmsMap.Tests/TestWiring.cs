using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using VmsMap.Adapter;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain.Arming;

namespace VmsMap.Tests;

/// <summary>
/// The pieces a device-aware service needs in a test. Settings are empty — a part with no row keeps
/// the defaults, which is exactly what the tests exercise — and a camera is disarmed unless a test
/// says otherwise.
/// </summary>
internal sealed class NoDeviceSettings : IDeviceSettingsSource
{
  public Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct) =>
    Task.FromResult<IReadOnlyList<DeviceSettingsInfo>>([]);

  public Task<DeviceSettingsInfo?> FindAsync(
    string cameraId, string kind, string token, CancellationToken ct) =>
    Task.FromResult<DeviceSettingsInfo?>(null);

  public void Invalidate(string cameraId) { }
}

internal sealed class TestCameraState(bool armed = false) : ICameraStateSource
{
  public bool Armed { get; set; } = armed;

  public Task<bool> GetArmedAsync(string cameraId, CancellationToken ct) =>
    Task.FromResult(Armed);
}

internal static class TestWiring
{
  public static DeviceNodeService DeviceNodes(
    VmsMapDbContext db, MapDb map, MapUpdateService updates, ICameraCatalog? catalog = null) =>
    new(db, map, updates, catalog ?? new AlwaysKnownCatalog(),
        new NoDeviceSettings(), NullLogger<DeviceNodeService>.Instance);

  public static EffectiveArmingResolver Arming(
    VmsMapDbContext db, ICameraStateSource? cameraState = null) =>
    new(db, new NoDeviceSettings(), cameraState ?? new TestCameraState());

  private sealed class AlwaysKnownCatalog : ICameraCatalog
  {
    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult<CameraInfo?>(new CameraInfo(cameraId, "Camera " + cameraId, "srv"));

    public Task<List<CameraInfo>> ListAsync(string? serverId, CancellationToken ct) =>
      Task.FromResult(new List<CameraInfo>());
  }
}
