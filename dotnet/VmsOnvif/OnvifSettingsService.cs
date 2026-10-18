using System.Collections.Concurrent;
using OnvifLib;
using web_srv.Types.Actors;

namespace VmsOnvif;

// Profile G ("edge recording") lives in OnvifEdgeSettings.cs and Profile M (metadata/analytics) in
// OnvifMetadataSettings.cs — same class, kept apart because each is a self-contained feature with
// its own service clients.
public sealed partial class OnvifSettingsService(
  CameraConnCache connCache,
  MetadataStateCache metadataStateCache,
  ILogger<OnvifSettingsService> logger)
{
  private readonly ConcurrentDictionary<string, (Camera Cam, DateTime Created)> _cache = new();
  private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

  private static string CacheKey(CameraConnInfo conn) => $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}:{conn.Xaddr}";

  private Camera GetOrCreate(CameraConnInfo conn)
  {
    var key = CacheKey(conn);
    var now = DateTime.UtcNow;
    if (_cache.TryGetValue(key, out var entry) && now - entry.Created < CacheTtl)
      return entry.Cam;
    // Two concurrent callers may both reach here; last write wins.
    // Camera holds no IDisposable resources so the abandoned instance is benign.
    // Xaddr must be passed (as CameraActor.EnsureCamera does): on cameras whose device service
    // is not at the default /onvif/device_service path, deriving the URL from ip:port resolves
    // no services at all.
    var cam = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass, xaddr: conn.Xaddr);
    _cache[key] = (cam, now);
    return cam;
  }

  private void Evict(CameraConnInfo conn) => _cache.TryRemove(CacheKey(conn), out _);

  // ── Video encoder ─────────────────────────────────────────────────────────────

  public Task<List<OnvifVideoEncoderConfig>> GetConfigsAsync(string cameraId, CancellationToken ct)
    => WithMedia(cameraId, "GetVideoEncoderConfigs", ct, m => m.GetVideoEncoderConfigsAsync(), []);

  public Task<List<OnvifVideoEncoderOptions>> GetConfigOptionsAsync(string cameraId, string configToken, CancellationToken ct)
    => WithMedia(cameraId, "GetVideoEncoderConfigOptions", ct, m => m.GetVideoEncoderConfigOptionsAsync(configToken), []);

  public Task SetConfigAsync(string cameraId, OnvifVideoEncoderConfig config, CancellationToken ct)
    => RequireMedia(cameraId, "SetVideoEncoderConfig", ct, m => m.SetVideoEncoderConfigAsync(config));

  // ── Audio encoder ─────────────────────────────────────────────────────────────

  public Task<List<OnvifAudioEncoderConfig>> GetAudioConfigsAsync(string cameraId, CancellationToken ct)
    => WithMedia(cameraId, "GetAudioEncoderConfigs", ct, m => m.GetAudioEncoderConfigsAsync(), []);

  public Task<List<OnvifAudioEncoderOption>> GetAudioConfigOptionsAsync(string cameraId, string configToken, CancellationToken ct)
    => WithMedia(cameraId, "GetAudioEncoderConfigOptions", ct, m => m.GetAudioEncoderConfigOptionsAsync(configToken), []);

  public Task SetAudioConfigAsync(string cameraId, OnvifAudioEncoderConfig config, CancellationToken ct)
    => RequireMedia(cameraId, "SetAudioEncoderConfig", ct, m => m.SetAudioEncoderConfigAsync(config));

  // ── PTZ presets ───────────────────────────────────────────────────────────────

  public Task<List<PtzPresetDto>> GetPresetsAsync(string cameraId, string profileToken, CancellationToken ct)
    => WithPtz(cameraId, "GetPresets", ct, p => p.GetPresetsAsync(profileToken), []);

  public Task<string> SetPresetAsync(string cameraId, string profileToken, string presetName, string presetToken, CancellationToken ct)
    => RequirePtz<string>(cameraId, "SetPreset", ct, p => p.SetPresetAsync(profileToken, presetName, presetToken));

  public Task GotoPresetAsync(string cameraId, string profileToken, string presetToken, CancellationToken ct)
    => RequirePtz(cameraId, "GotoPreset", ct, p => p.GotoPresetAsync(profileToken, presetToken));

  public Task RemovePresetAsync(string cameraId, string profileToken, string presetToken, CancellationToken ct)
    => RequirePtz(cameraId, "RemovePreset", ct, p => p.RemovePresetAsync(profileToken, presetToken));

  // ── Imaging ───────────────────────────────────────────────────────────────────

  public Task<OnvifImagingSettings?> GetImagingSettingsAsync(string cameraId, string videoSourceToken, CancellationToken ct)
    => WithImaging<OnvifImagingSettings?>(cameraId, "GetImagingSettings", ct, i => i.GetImagingSettingsAsync(videoSourceToken), null);

  public Task<OnvifImagingOptions?> GetImagingOptionsAsync(string cameraId, string videoSourceToken, CancellationToken ct)
    => WithImaging<OnvifImagingOptions?>(cameraId, "GetImagingOptions", ct, i => i.GetOptionsAsync(videoSourceToken), null);

  public Task SetImagingSettingsAsync(string cameraId, string videoSourceToken, OnvifImagingSettings settings, CancellationToken ct)
    => RequireImaging(cameraId, "SetImagingSettings", ct, i => i.SetImagingSettingsAsync(videoSourceToken, settings));

  // ── Device I/O: digital inputs ────────────────────────────────────────────────

  public Task<List<OnvifDigitalInput>> GetDigitalInputsAsync(string cameraId, CancellationToken ct)
    => WithDeviceIO(cameraId, "GetDigitalInputs", ct, d => d.GetDigitalInputsAsync(), []);

  public async Task<List<OnvifDigitalInputOptions>> GetDigitalInputOptionsAsync(string cameraId, CancellationToken ct)
  {
    // Resolve the Device I/O service once (rather than per token via WithDeviceIO) and list inputs
    // from it directly, avoiding a duplicate GetDigitalInputs + a fresh conn resolution per token.
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];
    var deviceIO = await GetDeviceIOOrNull(data.Conn);
    if (deviceIO == null) return [];

    var options = new List<OnvifDigitalInputOptions>();
    try
    {
      var inputs = await deviceIO.GetDigitalInputsAsync();
      foreach (var input in inputs)
      {
        try
        {
          var opt = await deviceIO.GetDigitalInputConfigurationOptionsAsync(input.Token);
          if (opt != null) options.Add(opt);
        }
        catch (Exception ex)
        {
          // Options are advisory (they only gate the idle-state editor). A camera that supports
          // GetDigitalInputs but faults on GetDigitalInputConfigurationOptions for one input must
          // not blank the whole list — treat that input as non-configurable and continue.
          logger.LogWarning(ex, "GetDigitalInputConfigurationOptions failed for camera {CameraId} input {Token}", cameraId, input.Token);
        }
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetDigitalInputOptions failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
    return options;
  }

  public Task SetDigitalInputIdleStateAsync(string cameraId, string token, string idleState, CancellationToken ct)
    => RequireDeviceIO(cameraId, "SetDigitalInputIdleState", ct, d => d.SetDigitalInputIdleStateAsync(token, idleState));

  // ── Device I/O: relay outputs (via Camera's Device Management passthrough) ───

  public async Task<List<OnvifRelayOutput>> GetRelayOutputsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];
    try { return await GetOrCreate(data.Conn).GetRelayOutputsAsync(); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetRelayOutputs failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task SetRelayOutputStateAsync(string cameraId, string token, bool active, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    try { await GetOrCreate(data.Conn).SetRelayOutputStateAsync(token, active); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetRelayOutputState failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task SetRelayOutputSettingsAsync(string cameraId, string token, string mode, string idleState, int delayMs, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    try { await GetOrCreate(data.Conn).SetRelayOutputSettingsAsync(token, mode, idleState, delayMs); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetRelayOutputSettings failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public Task<List<OnvifRelayOutputOptions>> GetRelayOutputOptionsAsync(string cameraId, CancellationToken ct)
    => WithDeviceIO(cameraId, "GetRelayOutputOptions", ct, d => d.GetRelayOutputOptionsAsync(), []);

  // Relay presence can't be gated on namespace alone (relays live under the always-present Device
  // Management service) — an actual call is required, same reasoning as PTZ's capability check.
  // Unlike PTZ this isn't cached in Camera.GetCapabilitiesSummaryAsync(): kept here as an
  // independent probe, mirroring how HasAudio is computed at the gRPC layer today.
  public async Task<bool> HasRelayOutputsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return false;
    try
    {
      // Deliberately does NOT evict on failure (unlike GetRelayOutputsAsync): this runs on every
      // capabilities probe, and a camera without relay hardware answering GetRelayOutputs with an
      // "ActionNotSupported" SOAP fault is expected, not a broken connection — evicting here would
      // defeat the Camera connection cache on every capabilities fetch for such cameras.
      return (await GetOrCreate(data.Conn).GetRelayOutputsAsync()).Count > 0;
    }
    catch
    {
      return false;
    }
  }

  // ── System ────────────────────────────────────────────────────────────────────

  public async Task<DateTime?> GetDeviceTimeAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return null;
    try
    {
      return await GetOrCreate(data.Conn).GetDeviceTimeAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetDeviceTime failed for camera {CameraId}", cameraId);
      return null;
    }
  }

  public async Task SyncDeviceTimeAsync(string cameraId, CancellationToken ct)
    => await SetDeviceTimeAsync(cameraId, DateTime.UtcNow, ct);

  public async Task SetDeviceTimeAsync(string cameraId, DateTime utcTime, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    try
    {
      await GetOrCreate(data.Conn).SetTimeAsync(utcTime);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetDeviceTime failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task<OnvifDeviceInfo?> GetDeviceInfoAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return null;
    try
    {
      return await GetOrCreate(data.Conn).GetDeviceInformationAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetDeviceInformation failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return null;
    }
  }

  public async Task<OnvifCapabilities?> GetCapabilitiesAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return null;
    try
    {
      return await GetOrCreate(data.Conn).GetCapabilitiesSummaryAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetCapabilities failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return null;
    }
  }

  public async Task<string> RebootDeviceAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    try
    {
      return await GetOrCreate(data.Conn).RebootAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "RebootDevice failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── Execute helpers ───────────────────────────────────────────────────────────

  private async Task<T> WithMedia<T>(string cameraId, string opName, CancellationToken ct,
    Func<MediaService, Task<T>> action, T fallback)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return fallback;
    var media = await GetMediaOrNull(data.Conn);
    if (media == null) return fallback;
    try { return await action(media); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task RequireMedia(string cameraId, string opName, CancellationToken ct,
    Func<MediaService, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var media = await GetMediaOrNull(data.Conn);
    if (media == null)
      throw new InvalidOperationException($"ONVIF media service unavailable for camera '{cameraId}'");
    try { await action(media); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<T> WithPtz<T>(string cameraId, string opName, CancellationToken ct,
    Func<PtzService2, Task<T>> action, T fallback)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return fallback;
    var ptz = await GetPtzOrNull(data.Conn);
    if (ptz == null) return fallback;
    try { return await action(ptz); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<T> RequirePtz<T>(string cameraId, string opName, CancellationToken ct,
    Func<PtzService2, Task<T>> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var ptz = await GetPtzOrNull(data.Conn);
    if (ptz == null)
      throw new InvalidOperationException($"ONVIF PTZ service unavailable for camera '{cameraId}'");
    try { return await action(ptz); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task RequirePtz(string cameraId, string opName, CancellationToken ct,
    Func<PtzService2, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var ptz = await GetPtzOrNull(data.Conn);
    if (ptz == null)
      throw new InvalidOperationException($"ONVIF PTZ service unavailable for camera '{cameraId}'");
    try { await action(ptz); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<T> WithImaging<T>(string cameraId, string opName, CancellationToken ct,
    Func<ImagingService2, Task<T>> action, T fallback)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return fallback;
    var imaging = await GetImagingOrNull(data.Conn);
    if (imaging == null) return fallback;
    try { return await action(imaging); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task RequireImaging(string cameraId, string opName, CancellationToken ct,
    Func<ImagingService2, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var imaging = await GetImagingOrNull(data.Conn);
    if (imaging == null)
      throw new InvalidOperationException($"ONVIF Imaging service unavailable for camera '{cameraId}'");
    try { await action(imaging); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<T> WithDeviceIO<T>(string cameraId, string opName, CancellationToken ct,
    Func<DeviceIOService2, Task<T>> action, T fallback)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return fallback;
    var deviceIO = await GetDeviceIOOrNull(data.Conn);
    if (deviceIO == null) return fallback;
    try { return await action(deviceIO); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task RequireDeviceIO(string cameraId, string opName, CancellationToken ct,
    Func<DeviceIOService2, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var deviceIO = await GetDeviceIOOrNull(data.Conn);
    if (deviceIO == null)
      throw new InvalidOperationException($"ONVIF Device I/O service unavailable for camera '{cameraId}'");
    try { await action(deviceIO); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── Low-level accessors ───────────────────────────────────────────────────────

  private async Task<DeviceIOService2?> GetDeviceIOOrNull(CameraConnInfo conn)
  {
    try
    {
      var cam = GetOrCreate(conn);
      var deviceIO = await cam.GetDeviceIOService();
      if (deviceIO == null) Evict(conn);
      return deviceIO;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF Device I/O service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }

  private async Task<MediaService?> GetMediaOrNull(CameraConnInfo conn)
  {
    try
    {
      var cam   = GetOrCreate(conn);
      var media = await cam.GetMediaService();
      if (media == null) Evict(conn);
      return media;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF media service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }

  private async Task<PtzService2?> GetPtzOrNull(CameraConnInfo conn)
  {
    try
    {
      var cam = GetOrCreate(conn);
      var ptz = await cam.GetPtzService();
      if (ptz == null) Evict(conn);
      return ptz;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF PTZ service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }

  private async Task<ImagingService2?> GetImagingOrNull(CameraConnInfo conn)
  {
    try
    {
      var cam = GetOrCreate(conn);
      var imaging = await cam.GetImagingService();
      if (imaging == null) Evict(conn);
      return imaging;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF Imaging service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }
}
