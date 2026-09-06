using System.Collections.Concurrent;
using Vms.Analytics;
using Vms.Cfg;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// What a camera follows when nobody has told it otherwise.
/// </summary>
/// <param name="Kinds">
/// In priority order. The first kind on the list that is present in the picture is the one taken.
/// Empty means any object will do.
/// </param>
/// <param name="Sources">
/// Which producers of boxes to believe. Empty means either will do.
/// </param>
public sealed record TrackingPolicy(
  bool Enabled,
  IReadOnlyList<DetectionKind> Kinds,
  IReadOnlySet<string> Sources)
{
  /// <summary>What a camera with no row in the table gets: switched off, nothing preferred.</summary>
  public static readonly TrackingPolicy Off =
    new(false, [], new HashSet<string>(StringComparer.Ordinal));

  /// <summary>A camera that is armed but has been told nothing about what to prefer.</summary>
  public static readonly TrackingPolicy Any =
    new(true, [], new HashSet<string>(StringComparer.Ordinal));

  public bool Accepts(string source) => Sources.Count == 0 || Sources.Contains(source);
}

/// <summary>
/// Cached self-guidance policy per camera, invalidated through the same pub/sub path as routes.
/// </summary>
/// <remarks>
/// This is also where the wire's words become the enum the control loop works in. VmsCfg stores the
/// kinds as strings, the way `analytics_watch.classes` has always been stored and the way the
/// browser wants them; everything past this boundary deals in <see cref="DetectionKind"/>, so the
/// translation happens once, here, rather than being repeated wherever a kind is compared.
/// </remarks>
public sealed class TrackingPolicyCache(
  CfgService.CfgServiceClient cfg,
  ILogger<TrackingPolicyCache> logger)
{
  private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

  private sealed record Entry(TrackingPolicy Policy, DateTime FetchedAt);

  private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

  public void Invalidate(IReadOnlyCollection<string> cameraIds)
  {
    if (cameraIds.Count == 0)
    {
      _entries.Clear();
      return;
    }

    foreach (var cameraId in cameraIds)
      _entries.TryRemove(cameraId, out _);
  }

  public async Task<TrackingPolicy> GetAsync(string cameraId, CancellationToken ct)
  {
    if (_entries.TryGetValue(cameraId, out var cached) && DateTime.UtcNow - cached.FetchedAt < Ttl)
      return cached.Policy;

    try
    {
      var response = await cfg.GetPtzTrackingPolicyAsync(
        new GetPtzTrackingPolicyRequest { CameraId = cameraId }, cancellationToken: ct);
      var policy = response.Found ? FromProto(response.Policy) : TrackingPolicy.Off;
      _entries[cameraId] = new Entry(policy, DateTime.UtcNow);
      return policy;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not load the tracking policy for camera '{Camera}'", cameraId);
      // A failed fetch is not an answer: keeping the previous one lets a camera go on tracking
      // through a VmsCfg hiccup rather than stopping dead. With nothing cached the safe reading is
      // "not configured to move".
      return cached?.Policy ?? TrackingPolicy.Off;
    }
  }

  /// <summary>Every camera that has a policy row, for the reconciler.</summary>
  /// <returns>Null when VmsCfg could not be read — which is not the same as "nobody tracks".</returns>
  public async Task<IReadOnlyDictionary<string, TrackingPolicy>?> ListAsync(CancellationToken ct)
  {
    try
    {
      var response = await cfg.ListPtzTrackingPoliciesAsync(
        new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: ct);
      var now = DateTime.UtcNow;
      var all = new Dictionary<string, TrackingPolicy>(StringComparer.Ordinal);
      foreach (var proto in response.Policies)
      {
        var policy = FromProto(proto);
        all[proto.CameraId] = policy;
        // The listing is a fetch like any other, so it may as well warm the per-camera cache the
        // sessions read from.
        _entries[proto.CameraId] = new Entry(policy, now);
      }
      return all;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Could not list tracking policies");
      return null;
    }
  }

  private static TrackingPolicy FromProto(PtzTrackingPolicy proto) =>
    new(
      proto.Enabled,
      proto.Kinds.Select(ParseKind).Where(k => k != DetectionKind.Unspecified).ToList(),
      proto.Sources.ToHashSet(StringComparer.Ordinal));

  // The same words the browser and `analytics_watch.classes` use. Copies of this table exist in
  // every service that has to cross the same boundary (AnalyticsKindMapper, DetectionKinds,
  // OnvifMetadataIngestor); they are not shared because these services have no project in common,
  // and because the mapping is a wire contract rather than logic — changing it is a contract change,
  // not a refactor.
  private static DetectionKind ParseKind(string kind) => kind.Trim().ToLowerInvariant() switch
  {
    "person" => DetectionKind.Person,
    "face" => DetectionKind.Face,
    "vehicle" => DetectionKind.Vehicle,
    "license_plate" => DetectionKind.LicensePlate,
    "animal" => DetectionKind.Animal,
    _ => DetectionKind.Unspecified,
  };
}
