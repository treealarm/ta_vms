
namespace VmsPtzTrackingSingleton.Geo;

/// <summary>
/// Cameras' spatial models, fetched and fitted once per change rather than per detection.
/// </summary>
/// <remarks>
/// <para>
/// Here because a slave aimed through the map uses two of these on every detection of its master,
/// several times a second. The wizards and the operator's map keep going to
/// <see cref="CameraSpatialSource"/> directly: each needs the configuration as it is this moment,
/// and none of them runs often enough for that to cost anything.
/// </para>
/// <para>
/// A model is made of two kinds of configuration, and each has its own event. A camera's placement,
/// observations and marks travel on <c>server-config-changed</c> with the camera's id, which marks
/// that camera stale. A landmark or a space travels on <c>space-config-changed</c>, which names no
/// camera -- any number of them may have been calibrated against that landmark -- so it marks them
/// all. See <see cref="TtlCache{T}"/> for what a failed read does.
/// </para>
/// </remarks>
public sealed class SpatialModelCache(CameraSpatialSource source, ILogger<SpatialModelCache> logger)
{
  private readonly TtlCache<CameraSpatialModel> _cache =
    new(source.LoadAsync, TimeSpan.FromMinutes(1), logger, "spatial model");

  /// <param name="cameraIds">The cameras to re-read; empty re-reads every one.</param>
  public void Invalidate(IReadOnlyCollection<string> cameraIds) => _cache.Invalidate(cameraIds);

  /// <returns>A null value when the camera has no reference placement, unless the read failed.</returns>
  public Task<CachedLookup<CameraSpatialModel>> LookupAsync(string cameraId, bool fresh, CancellationToken ct) =>
    _cache.LookupAsync(cameraId, fresh, ct);
}
