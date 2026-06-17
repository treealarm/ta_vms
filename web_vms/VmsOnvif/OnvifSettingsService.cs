using System.Collections.Concurrent;
using OnvifLib;
using web_vms.Types.Actors;

namespace VmsOnvif;

public sealed class OnvifSettingsService(CameraConnCache connCache, ILogger<OnvifSettingsService> logger)
{
  private readonly ConcurrentDictionary<string, (Camera Cam, DateTime Created)> _cache = new();
  private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

  private Camera GetOrCreate(CameraConnInfo conn)
  {
    var key = $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}";
    var now = DateTime.UtcNow;
    if (_cache.TryGetValue(key, out var entry) && now - entry.Created < CacheTtl)
      return entry.Cam;
    var cam = Camera.Create(conn.Ip, conn.Port, conn.User, conn.Pass);
    _cache[key] = (cam, now);
    return cam;
  }

  private void Evict(CameraConnInfo conn)
  {
    var key = $"{conn.Ip}:{conn.Port}:{conn.User}:{conn.Pass}";
    _cache.TryRemove(key, out _);
  }

  public async Task<List<OnvifVideoEncoderConfig>> GetConfigsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];

    var media = await GetMediaOrNull(data.Conn);
    if (media == null) return [];

    try
    {
      return await media.GetVideoEncoderConfigsAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetVideoEncoderConfigs failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task<List<OnvifVideoEncoderOptions>> GetConfigOptionsAsync(
    string cameraId, string configToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];

    var media = await GetMediaOrNull(data.Conn);
    if (media == null) return [];

    try
    {
      return await media.GetVideoEncoderConfigOptionsAsync(configToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetVideoEncoderConfigOptions failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task SetConfigAsync(string cameraId, OnvifVideoEncoderConfig config, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");

    var media = await GetMediaOrNull(data.Conn);
    if (media == null)
      throw new InvalidOperationException($"ONVIF media service unavailable for camera '{cameraId}'");

    try
    {
      await media.SetVideoEncoderConfigAsync(config);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetVideoEncoderConfig failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── Audio encoder ─────────────────────────────────────────────────────────

  public async Task<List<OnvifAudioEncoderConfig>> GetAudioConfigsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];
    var media = await GetMediaOrNull(data.Conn);
    if (media == null) return [];
    try
    {
      return await media.GetAudioEncoderConfigsAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetAudioEncoderConfigs failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task<List<OnvifAudioEncoderOption>> GetAudioConfigOptionsAsync(
    string cameraId, string configToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];
    var media = await GetMediaOrNull(data.Conn);
    if (media == null) return [];
    try
    {
      return await media.GetAudioEncoderConfigOptionsAsync(configToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetAudioEncoderConfigOptions failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task SetAudioConfigAsync(string cameraId, OnvifAudioEncoderConfig config, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var media = await GetMediaOrNull(data.Conn);
    if (media == null)
      throw new InvalidOperationException($"ONVIF media service unavailable for camera '{cameraId}'");
    try
    {
      await media.SetAudioEncoderConfigAsync(config);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetAudioEncoderConfig failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── PTZ presets ────────────────────────────────────────────────────────────

  public async Task<List<PtzPresetDto>> GetPresetsAsync(string cameraId, string profileToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return [];
    var ptz = await GetPtzOrNull(data.Conn);
    if (ptz == null) return [];
    return await ptz.GetPresetsAsync(profileToken);
  }

  public async Task<string> SetPresetAsync(
    string cameraId, string profileToken, string presetName, string presetToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var ptz = await GetPtzOrNull(data.Conn)
      ?? throw new InvalidOperationException($"ONVIF PTZ service unavailable for camera '{cameraId}'");
    try
    {
      return await ptz.SetPresetAsync(profileToken, presetName, presetToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetPreset failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task GotoPresetAsync(string cameraId, string profileToken, string presetToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var ptz = await GetPtzOrNull(data.Conn)
      ?? throw new InvalidOperationException($"ONVIF PTZ service unavailable for camera '{cameraId}'");
    try
    {
      await ptz.GotoPresetAsync(profileToken, presetToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GotoPreset failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task RemovePresetAsync(string cameraId, string profileToken, string presetToken, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var ptz = await GetPtzOrNull(data.Conn)
      ?? throw new InvalidOperationException($"ONVIF PTZ service unavailable for camera '{cameraId}'");
    try
    {
      await ptz.RemovePresetAsync(profileToken, presetToken);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "RemovePreset failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── System ─────────────────────────────────────────────────────────────────

  public async Task<DateTime?> GetDeviceTimeAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null) return null;
    try
    {
      var cam = GetOrCreate(data.Conn);
      return await cam.GetDeviceTimeAsync();
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
      var cam = GetOrCreate(data.Conn);
      await cam.SetTimeAsync(utcTime);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "SetDeviceTime failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  public async Task<string> RebootDeviceAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    try
    {
      var cam = GetOrCreate(data.Conn);
      return await cam.RebootAsync();
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "RebootDevice failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

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
}
