using System.Collections.Concurrent;
using Grpc.Core;
using Vms.Cfg;

namespace VmsOnvif;

/// <summary>
/// Whether this camera's own object boxes are wanted at all, cached per camera.
/// </summary>
/// <remarks>
/// Same shape as <see cref="CameraArmedCache"/> and <see cref="OnvifFilterCache"/>, and for the
/// same reason: this is read once per metadata document — several times a second per camera —
/// and a gRPC call on that path would cost more than the parse it is meant to avoid.
/// Invalidated by <see cref="CameraConfigSubscriber"/> on server-config-changed.
///
/// Fails open. A VmsCfg hiccup must not silently stop metadata a deployment depends on: the
/// setting is an opt-in to dropping data, so not knowing its value means behaving as before.
/// </remarks>
public sealed class OnvifMetadataGateCache(CfgService.CfgServiceClient cfgClient)
{
  private static readonly TimeSpan CfgTimeout = TimeSpan.FromSeconds(5);
  private readonly ConcurrentDictionary<string, bool> _cache = new();

  /// <summary>True when this camera's ONVIF metadata should be dropped without parsing.</summary>
  public async Task<bool> IsDisabledAsync(string cameraId, CancellationToken ct)
  {
    if (_cache.TryGetValue(cameraId, out var cached))
      return cached;

    var opts = new CallOptions(deadline: DateTime.UtcNow.Add(CfgTimeout), cancellationToken: ct);
    try
    {
      var resp = await cfgClient.GetCameraAsync(new SourceRequest { SourceId = cameraId }, opts);
      var disabled = resp.Found && resp.Setup is not null && resp.Setup.OnvifMetadataDisabled;
      _cache[cameraId] = disabled;
      return disabled;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine(
        $"OnvifMetadataGateCache: failed to fetch setup for '{cameraId}': {ex.Message}");
      return false;
    }
  }

  public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

  public void InvalidateAll() => _cache.Clear();
}
