using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvif;

/// <summary>
/// ONVIF Profile M — the camera's own metadata track and the analytics engine behind it.
/// </summary>
/// <remarks>
/// Two things have to be true before a camera streams Scene Description alongside its video: a
/// MetadataConfiguration with Analytics=true must be attached to the media profile the stream is
/// pulled from, and a VideoAnalyticsConfiguration must be attached to the same profile. The first
/// is what puts a metadata track in the RTSP session at all; the second is what fills it with
/// objects. The UI drives both through here.
///
/// Reads answer with an empty result rather than throwing when the camera simply has no analytics
/// service — most cameras in a deployment do not. Writes throw: they are explicit operator actions
/// on the device.
/// </remarks>
public sealed partial class OnvifSettingsService
{
  // ── Metadata configurations ───────────────────────────────────────────────────

  public Task<List<OnvifMetadataConfig>> GetMetadataConfigsAsync(string cameraId, CancellationToken ct)
    => WithMedia(cameraId, "GetMetadataConfigs", ct, m => m.GetMetadataConfigsAsync(), []);

  public Task<OnvifMetadataConfigOptions?> GetMetadataConfigOptionsAsync(
    string cameraId, string configToken, CancellationToken ct)
    => WithMedia<OnvifMetadataConfigOptions?>(
      cameraId, "GetMetadataConfigOptions", ct, m => m.GetMetadataConfigOptionsAsync(configToken), null);

  public async Task SetMetadataConfigAsync(
    string cameraId, OnvifMetadataConfigUpdate update, CancellationToken ct)
  {
    await RequireMedia(cameraId, "SetMetadataConfig", ct, m => m.SetMetadataConfigAsync(update));
    // The whole point of this call is usually to flip Analytics, which is exactly what the
    // monitor's overlay button is gated on — so the probe cache must not keep serving the old
    // answer for its TTL.
    metadataStateCache.Invalidate(cameraId);
  }

  public Task<List<OnvifAnalyticsConfig>> GetAnalyticsConfigsAsync(string cameraId, CancellationToken ct)
    => WithMedia(cameraId, "GetAnalyticsConfigs", ct, m => m.GetAnalyticsConfigsAsync(), []);

  public async Task AttachMetadataConfigAsync(
    string cameraId, string profileToken, string configToken, CancellationToken ct)
  {
    await RequireMedia(cameraId, "AttachMetadataConfig", ct,
      m => m.AttachMetadataConfigAsync(profileToken, configToken));
    metadataStateCache.Invalidate(cameraId);
  }

  public async Task DetachMetadataConfigAsync(
    string cameraId, string profileToken, string configToken, CancellationToken ct)
  {
    await RequireMedia(cameraId, "DetachMetadataConfig", ct,
      m => m.DetachMetadataConfigAsync(profileToken, configToken));
    metadataStateCache.Invalidate(cameraId);
  }

  public async Task AttachAnalyticsConfigAsync(
    string cameraId, string profileToken, string configToken, CancellationToken ct)
  {
    await RequireMedia(cameraId, "AttachAnalyticsConfig", ct,
      m => m.AttachAnalyticsConfigAsync(profileToken, configToken));
    metadataStateCache.Invalidate(cameraId);
  }

  public async Task DetachAnalyticsConfigAsync(
    string cameraId, string profileToken, string configToken, CancellationToken ct)
  {
    await RequireMedia(cameraId, "DetachAnalyticsConfig", ct,
      m => m.DetachAnalyticsConfigAsync(profileToken, configToken));
    metadataStateCache.Invalidate(cameraId);
  }

  // ── Analytics modules and rules ───────────────────────────────────────────────

  public Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedAnalyticsModulesAsync(
    string cameraId, string configToken, CancellationToken ct)
    => WithAnalytics(cameraId, "GetSupportedAnalyticsModules", ct,
      a => a.GetSupportedAnalyticsModulesAsync(configToken), []);

  public Task<IReadOnlyList<OnvifAnalyticsModule>> GetAnalyticsModulesAsync(
    string cameraId, string configToken, CancellationToken ct)
    => WithAnalytics(cameraId, "GetAnalyticsModules", ct,
      a => a.GetAnalyticsModulesAsync(configToken), []);

  public Task CreateAnalyticsModuleAsync(
    string cameraId, string configToken, OnvifAnalyticsModule module, CancellationToken ct)
    => RequireAnalytics(cameraId, "CreateAnalyticsModule", ct,
      a => a.CreateAnalyticsModuleAsync(configToken, module));

  public Task ModifyAnalyticsModuleAsync(
    string cameraId, string configToken, OnvifAnalyticsModule module, CancellationToken ct)
    => RequireAnalytics(cameraId, "ModifyAnalyticsModule", ct,
      a => a.ModifyAnalyticsModuleAsync(configToken, module));

  public Task DeleteAnalyticsModuleAsync(
    string cameraId, string configToken, string moduleName, CancellationToken ct)
    => RequireAnalytics(cameraId, "DeleteAnalyticsModule", ct,
      a => a.DeleteAnalyticsModuleAsync(configToken, moduleName));

  public Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedRulesAsync(
    string cameraId, string configToken, CancellationToken ct)
    => WithAnalytics(cameraId, "GetSupportedRules", ct,
      a => a.GetSupportedRulesAsync(configToken), []);

  public Task<IReadOnlyList<OnvifAnalyticsModule>> GetRulesAsync(
    string cameraId, string configToken, CancellationToken ct)
    => WithAnalytics(cameraId, "GetRules", ct, a => a.GetRulesAsync(configToken), []);

  public Task CreateRuleAsync(
    string cameraId, string configToken, OnvifAnalyticsModule rule, CancellationToken ct)
    => RequireAnalytics(cameraId, "CreateRule", ct, a => a.CreateRuleAsync(configToken, rule));

  public Task ModifyRuleAsync(
    string cameraId, string configToken, OnvifAnalyticsModule rule, CancellationToken ct)
    => RequireAnalytics(cameraId, "ModifyRule", ct, a => a.ModifyRuleAsync(configToken, rule));

  public Task DeleteRuleAsync(
    string cameraId, string configToken, string ruleName, CancellationToken ct)
    => RequireAnalytics(cameraId, "DeleteRule", ct, a => a.DeleteRuleAsync(configToken, ruleName));

  // ── Overlay-button probe ──────────────────────────────────────────────────────

  /// <summary>
  /// Whether the camera is configured right now to stream object boxes. Talks to the camera —
  /// callers that cannot wait should go through <see cref="MetadataStateCache"/>.
  /// </summary>
  /// <remarks>
  /// Both halves have to meet on the same profile. A MetadataConfiguration with Analytics=true puts
  /// a metadata track in that profile's RTSP session, but nothing fills it unless a
  /// VideoAnalyticsConfiguration is attached to the same profile — the engine is what produces the
  /// objects. Checking only the metadata half lights the monitor's overlay button for a camera that
  /// will never draw anything.
  /// </remarks>
  public async Task<bool> ProbeBboxCapableAsync(string cameraId, CancellationToken ct)
  {
    var metadata = await GetMetadataConfigsAsync(cameraId, ct);
    var withAnalytics = metadata
      .Where(c => c.Analytics)
      .SelectMany(c => c.AttachedProfileTokens)
      .ToHashSet(StringComparer.Ordinal);

    if (withAnalytics.Count == 0)
      return false;

    var engines = await GetAnalyticsConfigsAsync(cameraId, ct);
    return engines.Any(e => e.AttachedProfileTokens.Any(withAnalytics.Contains));
  }

  // ── Execute helpers ───────────────────────────────────────────────────────────

  private async Task<T> WithAnalytics<T>(string cameraId, string opName, CancellationToken ct,
    Func<AnalyticsService, Task<T>> action, T fallback)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return fallback;
    var analytics = await GetAnalyticsOrNull(data.Conn);
    if (analytics == null) return fallback;
    try { return await action(analytics); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task RequireAnalytics(string cameraId, string opName, CancellationToken ct,
    Func<AnalyticsService, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var analytics = await GetAnalyticsOrNull(data.Conn);
    if (analytics == null)
      throw new InvalidOperationException($"ONVIF Analytics service unavailable for camera '{cameraId}'");
    try { await action(analytics); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<AnalyticsService?> GetAnalyticsOrNull(CameraConnInfo conn)
  {
    try
    {
      var cam = GetOrCreate(conn);
      var analytics = await cam.GetAnalyticsService();
      if (analytics == null) Evict(conn);
      return analytics;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF Analytics service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }
}
