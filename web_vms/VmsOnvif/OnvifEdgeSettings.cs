using System.Collections.Concurrent;
using OnvifLib;
using web_vms.Types.Actors;

namespace VmsOnvif;

/// <summary>
/// ONVIF Profile G — what the camera keeps on its own storage (SD card): capabilities, recordings,
/// searchable intervals, replay URIs, on-camera recording jobs and storage configuration.
/// </summary>
/// <remarks>
/// Read calls return an empty/unsupported result with a message instead of throwing: the UI has to
/// tell "this camera cannot do Profile G" apart from "the service is broken", and most cameras in a
/// deployment will not support it at all. Write calls (job mode, configuration, deletes, format)
/// do throw — they are explicit operator actions on the device and silence would be wrong.
/// </remarks>
public sealed partial class OnvifSettingsService
{
  // FindRecordings costs several SOAP round trips and the browse UI re-queries it on every window
  // change. VmsOnvif is single-instance by contract, so a process-wide cache is safe — but the key
  // includes an operator-chosen window, so entries must be reclaimed or the dictionary grows without
  // bound for as long as the service runs.
  private readonly ConcurrentDictionary<string, (List<OnvifEdgeInterval> Intervals, DateTime At)> _edgeIntervalCache = new();
  private static readonly TimeSpan EdgeIntervalCacheTtl = TimeSpan.FromSeconds(60);
  private const int EdgeIntervalCacheMaxEntries = 256;

  private void StoreIntervals(string key, List<OnvifEdgeInterval> intervals)
  {
    var now = DateTime.UtcNow;
    _edgeIntervalCache[key] = (intervals, now);

    if (_edgeIntervalCache.Count <= EdgeIntervalCacheMaxEntries)
      return;

    // Drop what the TTL already invalidated; if that is not enough (a burst of distinct windows
    // inside one TTL), drop the oldest until the cap holds.
    foreach (var stale in _edgeIntervalCache.Where(e => now - e.Value.At >= EdgeIntervalCacheTtl).ToList())
      _edgeIntervalCache.TryRemove(stale.Key, out _);

    foreach (var oldest in _edgeIntervalCache
               .OrderBy(e => e.Value.At)
               .Take(Math.Max(0, _edgeIntervalCache.Count - EdgeIntervalCacheMaxEntries))
               .ToList())
      _edgeIntervalCache.TryRemove(oldest.Key, out _);
  }

  public async Task<(OnvifEdgeCapabilities? Caps, string Message)> GetEdgeCapabilitiesAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return (null, $"No ONVIF connection configured for camera '{cameraId}'");

    try
    {
      var cam = GetOrCreate(data.Conn);
      var services = await cam.GetServicesAsync();
      if (services == null)
        return (null, "Camera did not answer GetServices");

      var hasRecording = services.ContainsKey(RecordingService.WSDL_V10);
      var hasSearch = services.ContainsKey(SearchService.WSDL_V10);
      var hasReplay = services.ContainsKey(ReplayService.WSDL_V10);

      var dynamicRecordings = false;
      var dynamicTracks = false;
      var maxRecordings = 0;
      if (hasRecording)
      {
        var recording = await cam.GetRecordingService();
        if (recording != null)
          (dynamicRecordings, dynamicTracks, maxRecordings) = await recording.GetServiceCapabilitiesAsync();
      }

      // Storage support sits on Device Management and is independent of Profile G — a camera can
      // report a card without exposing recordings, and vice versa.
      var storage = new OnvifDeviceStorageSupport(false, 0, []);
      try { storage = await cam.GetStorageSupportAsync(); }
      catch (Exception ex) { logger.LogDebug(ex, "GetStorageSupport failed for camera {CameraId}", cameraId); }

      return (new OnvifEdgeCapabilities(
        Recording: hasRecording,
        Search: hasSearch,
        Replay: hasReplay,
        DynamicRecordings: dynamicRecordings,
        DynamicTracks: dynamicTracks,
        StorageConfiguration: storage.StorageConfiguration,
        AuxiliaryCommands: storage.AuxiliaryCommands.Count > 0,
        MaxRecordings: maxRecordings,
        SupportedAuxiliaryCommands: storage.AuxiliaryCommands), string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetEdgeCapabilities failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return (null, ex.Message);
    }
  }

  public async Task<(OnvifEdgeRecordingSummary? Summary, TimeSpan? ClockOffset, string Message)>
    GetEdgeRecordingSummaryAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return (null, null, $"No ONVIF connection configured for camera '{cameraId}'");

    var search = await GetSearchOrNull(data.Conn);
    if (search == null)
      return (null, null, "Camera does not expose the ONVIF RecordingSearch service");

    var offset = await GetClockOffsetAsync(cameraId, ct);
    try
    {
      var summary = await search.GetRecordingSummaryAsync();
      return (ToServerTime(summary, offset), offset, string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetEdgeRecordingSummary failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return (null, offset, ex.Message);
    }
  }

  /// <summary>
  /// Lists the camera's recordings. Tokens come from the Recording service; per-recording tracks and
  /// time span come from RecordingSearch, which is the only place the device reports them.
  /// </summary>
  public async Task<(List<OnvifEdgeRecording> Recordings, TimeSpan? ClockOffset, string Message)>
    ListEdgeRecordingsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return ([], null, $"No ONVIF connection configured for camera '{cameraId}'");

    var offset = await GetClockOffsetAsync(cameraId, ct);
    try
    {
      var cam = GetOrCreate(data.Conn);
      var recording = await cam.GetRecordingService();
      if (recording == null)
        return ([], offset, "Camera does not expose the ONVIF Recording service");

      var configs = await recording.GetRecordingsAsync();
      var search = await GetSearchOrNull(data.Conn);

      var result = new List<OnvifEdgeRecording>();
      foreach (var config in configs)
      {
        ct.ThrowIfCancellationRequested();
        OnvifEdgeRecording? info = null;
        if (search != null)
        {
          try { info = await search.GetRecordingInformationAsync(config.RecordingToken); }
          catch (Exception ex)
          {
            // One recording failing to describe itself must not blank the whole list.
            logger.LogDebug(ex, "GetRecordingInformation failed for camera {CameraId} recording {Token}",
              cameraId, config.RecordingToken);
          }
        }

        result.Add(info ?? new OnvifEdgeRecording(
          config.RecordingToken,
          config.SourceName,
          config.SourceDescription,
          null, null,
          config.Content,
          string.Empty,
          []));
      }
      return (result.Select(r => ToServerTime(r, offset)).ToList(), offset, string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ListEdgeRecordings failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return ([], offset, ex.Message);
    }
  }

  /// <summary>
  /// Recorded spans on the card. The window comes in — and the spans go out — in server time; the
  /// camera only ever sees its own clock (see <see cref="GetClockOffsetAsync"/>).
  /// </summary>
  public async Task<(List<OnvifEdgeInterval> Intervals, bool Searched, TimeSpan? ClockOffset, string Message)>
    ListEdgeIntervalsAsync(
      string cameraId, DateTime? from, DateTime? to, string? recordingToken, int maxResults, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return ([], false, null, $"No ONVIF connection configured for camera '{cameraId}'");

    var search = await GetSearchOrNull(data.Conn);
    if (search == null)
      return ([], false, null, "Camera does not expose the ONVIF RecordingSearch service");

    var offset = await GetClockOffsetAsync(cameraId, ct);
    try
    {
      var cameraFrom = ToCameraTime(from, offset);
      var cameraTo = ToCameraTime(to, offset);

      // An unbounded request means "whatever is on the card" — resolve it against the summary so
      // the XPath filter always carries a real window. Summary is the device's own scale, which is
      // the scale of everything below this line.
      if (cameraFrom == null || cameraTo == null)
      {
        var summary = await search.GetRecordingSummaryAsync();
        cameraFrom ??= summary?.DataFrom;
        cameraTo ??= summary?.DataUntil;
        if (cameraFrom == null || cameraTo == null)
          return ([], true, offset, string.Empty);
      }

      // Keyed on the camera-scale window: the same operator window measured against a slightly
      // different offset is a different query to the device, and reusing the old answer for it
      // would show spans that are off by the difference.
      var key = $"{cameraId}|{recordingToken}|{cameraFrom:O}|{cameraTo:O}|{maxResults}";
      if (_edgeIntervalCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < EdgeIntervalCacheTtl)
        return (ToServerTime(hit.Intervals, offset), true, offset, string.Empty);

      var found = await search.FindRecordingsAsync(cameraFrom.Value, cameraTo.Value, recordingToken, maxResults, ct);
      var intervals = SearchService.ToIntervals(found, cameraTo);
      // Cached in camera scale, converted on the way out: the cache then survives a re-measured
      // offset instead of holding answers corrected by a value nobody remembers.
      StoreIntervals(key, intervals);
      return (ToServerTime(intervals, offset), true, offset, string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ListEdgeIntervals failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return ([], false, offset, ex.Message);
    }
  }

  /// <summary>
  /// Resolves a replay URI. Devices commonly hand out single-use URIs, so callers must fetch one
  /// immediately before starting a pull rather than caching it.
  /// </summary>
  public async Task<(string Uri, TimeSpan? ClockOffset, string Message)> GetEdgeReplayUriAsync(
    string cameraId, string recordingToken, string transport, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return (string.Empty, null, $"No ONVIF connection configured for camera '{cameraId}'");

    // Reported alongside the URI so the caller starting a pull can refuse to guess: an unknown
    // offset means an unknown position on the card.
    var offset = await GetClockOffsetAsync(cameraId, ct);
    try
    {
      var replay = await GetReplayOrNull(data.Conn);
      if (replay == null)
        return (string.Empty, offset, "Camera does not expose the ONVIF Replay service");
      var uri = await replay.GetReplayUriAsync(recordingToken, string.IsNullOrWhiteSpace(transport) ? "RTSP" : transport);
      return (uri, offset, string.IsNullOrWhiteSpace(uri) ? "Camera returned an empty replay uri" : string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetEdgeReplayUri failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return (string.Empty, offset, ex.Message);
    }
  }

  // ── On-camera recording jobs and configuration ───────────────────────────────

  public async Task<(List<OnvifEdgeRecordingJob> Jobs, List<OnvifEdgeRecordingConfiguration> Configurations, string Message)>
    ListEdgeRecordingJobsAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return ([], [], $"No ONVIF connection configured for camera '{cameraId}'");

    try
    {
      var recording = await GetRecordingOrNull(data.Conn);
      if (recording == null)
        return ([], [], "Camera does not expose the ONVIF Recording service");
      return (await recording.GetRecordingJobsAsync(), await recording.GetRecordingsAsync(), string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ListEdgeRecordingJobs failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return ([], [], ex.Message);
    }
  }

  public Task SetEdgeRecordingJobModeAsync(string cameraId, string jobToken, string mode, CancellationToken ct)
    => RequireRecording(cameraId, "SetEdgeRecordingJobMode", ct, r => r.SetRecordingJobModeAsync(jobToken, mode));

  public Task SetEdgeRecordingConfigurationAsync(string cameraId, string recordingToken, string content, string maximumRetentionTime, CancellationToken ct)
    => RequireRecording(cameraId, "SetEdgeRecordingConfiguration", ct, r => r.SetRecordingConfigurationAsync(recordingToken, content, maximumRetentionTime));

  public Task DeleteEdgeRecordingAsync(string cameraId, string recordingToken, CancellationToken ct)
    => RequireRecording(cameraId, "DeleteEdgeRecording", ct, r => r.DeleteRecordingAsync(recordingToken));

  public Task DeleteEdgeTrackAsync(string cameraId, string recordingToken, string trackToken, CancellationToken ct)
    => RequireRecording(cameraId, "DeleteEdgeTrack", ct, r => r.DeleteTrackAsync(recordingToken, trackToken));

  public Task DeleteEdgeRecordingJobAsync(string cameraId, string jobToken, CancellationToken ct)
    => RequireRecording(cameraId, "DeleteEdgeRecordingJob", ct, r => r.DeleteRecordingJobAsync(jobToken));

  // ── Camera-side storage ──────────────────────────────────────────────────────

  public async Task<(List<OnvifEdgeStorageConfiguration> Configurations, bool Supported, string FormatCommand, string Message)>
    ListEdgeStorageAsync(string cameraId, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      return ([], false, string.Empty, $"No ONVIF connection configured for camera '{cameraId}'");

    try
    {
      var cam = GetOrCreate(data.Conn);
      var support = await cam.GetStorageSupportAsync();
      var formatCommand = PickFormatCommand(support.AuxiliaryCommands);

      if (!support.StorageConfiguration)
        return ([], false, formatCommand, "Camera does not expose storage configuration");

      return (await cam.GetStorageConfigurationsAsync(), true, formatCommand, string.Empty);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ListEdgeStorage failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      return ([], false, string.Empty, ex.Message);
    }
  }

  /// <summary>
  /// Formats camera-side storage via a vendor auxiliary command. Irreversible and destructive —
  /// the caller is responsible for confirming with the operator first.
  /// </summary>
  public async Task<string> FormatEdgeStorageAsync(string cameraId, string command, CancellationToken ct)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");

    var cam = GetOrCreate(data.Conn);
    if (string.IsNullOrWhiteSpace(command))
    {
      var support = await cam.GetStorageSupportAsync();
      command = PickFormatCommand(support.AuxiliaryCommands);
      if (string.IsNullOrWhiteSpace(command))
        throw new InvalidOperationException($"Camera '{cameraId}' advertises no auxiliary command that formats storage");
    }

    logger.LogWarning("Formatting camera-side storage on {CameraId} with auxiliary command '{Command}'", cameraId, command);
    try { return await cam.SendAuxiliaryCommandAsync(command); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "FormatEdgeStorage failed for camera {CameraId}", cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  // ONVIF standardises the transport for auxiliary commands but not the strings, so the format
  // command has to be recognised heuristically from what the device advertises.
  private static string PickFormatCommand(IEnumerable<string> commands) =>
    commands.FirstOrDefault(c => c.Contains("format", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

  // ── Plumbing ─────────────────────────────────────────────────────────────────

  private async Task RequireRecording(string cameraId, string opName, CancellationToken ct,
    Func<RecordingService, Task> action)
  {
    var data = await connCache.GetAsync(cameraId, ct);
    if (data?.Conn == null)
      throw new InvalidOperationException($"No ONVIF connection configured for camera '{cameraId}'");
    var recording = await GetRecordingOrNull(data.Conn);
    if (recording == null)
      throw new InvalidOperationException($"ONVIF Recording service unavailable for camera '{cameraId}'");
    try { await action(recording); }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "{Op} failed for camera {CameraId}", opName, cameraId);
      Evict(data.Conn);
      throw;
    }
  }

  private async Task<RecordingService?> GetRecordingOrNull(CameraConnInfo conn)
  {
    try
    {
      var service = await GetOrCreate(conn).GetRecordingService();
      if (service == null) Evict(conn);
      return service;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF Recording service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }

  private async Task<SearchService?> GetSearchOrNull(CameraConnInfo conn)
  {
    try
    {
      var service = await GetOrCreate(conn).GetSearchService();
      if (service == null) Evict(conn);
      return service;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF RecordingSearch service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }

  private async Task<ReplayService?> GetReplayOrNull(CameraConnInfo conn)
  {
    try
    {
      var service = await GetOrCreate(conn).GetReplayService();
      if (service == null) Evict(conn);
      return service;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "ONVIF Replay service init failed for {Ip}:{Port}", conn.Ip, conn.Port);
      Evict(conn);
      return null;
    }
  }
}
