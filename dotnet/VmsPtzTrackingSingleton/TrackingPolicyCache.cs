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
/// <param name="MasterCameraId">
/// Whose detections to follow. Null is the camera's own, and the only case that closes a loop:
/// anything else names a master this camera is aimed at through a survey.
/// </param>
public sealed record TrackingPolicy(
  bool Enabled,
  IReadOnlyList<DetectionKind> Kinds,
  IReadOnlySet<string> Sources,
  string? MasterCameraId)
{
  public bool Accepts(string source) => Sources.Count == 0 || Sources.Contains(source);
}

/// <summary>
/// Reads the self-guidance policy of every camera that has one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a cache. A per-camera TTL entry was the obvious shape, copied from
/// <see cref="PtzCalibrationCache"/>, and it turned out to have no readers: a policy reaches a
/// session only through <see cref="TrackingReconciler"/>, which wants every camera at once and
/// wants it fresh. Keeping the entries would have meant maintaining invalidation for a value
/// nobody looked up.
/// </para>
/// <para>
/// This is also where the wire's words become the enum the control loop works in. VmsCfg stores the
/// kinds as strings, the way `analytics_watch.classes` has always been stored and the way the
/// browser wants them; everything past this boundary deals in <see cref="DetectionKind"/>, so the
/// translation happens once, here, rather than being repeated wherever a kind is compared.
/// </para>
/// </remarks>
public sealed class TrackingPolicyCache(
  CfgService.CfgServiceClient cfg,
  ILogger<TrackingPolicyCache> logger)
{
  /// <summary>Every camera that has a policy row, for the reconciler.</summary>
  /// <returns>Null when VmsCfg could not be read — which is not the same as "nobody tracks".</returns>
  public async Task<IReadOnlyDictionary<string, TrackingPolicy>?> ListAsync(CancellationToken ct)
  {
    try
    {
      var response = await cfg.ListPtzTrackingPoliciesAsync(
        new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: ct);
      var all = new Dictionary<string, TrackingPolicy>(StringComparer.Ordinal);
      foreach (var proto in response.Policies)
        all[proto.CameraId] = FromProto(proto);
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
      proto.Sources.ToHashSet(StringComparer.Ordinal),
      proto.HasMasterCameraId && proto.MasterCameraId.Length > 0 ? proto.MasterCameraId : null);

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
