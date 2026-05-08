namespace VmsFs;

public sealed class ClipCleaner(
  ClipStorage storage,
  CameraConfigCache configCache,
  StorageListCache storageListCache) : IHostedService, IAsyncDisposable
{
  // Conservative lower bound: SQL pre-filters clips older than this many seconds.
  // Per-camera pre_record_seconds is then applied in code as the authoritative check.
  private const int SqlMinOlderThanSeconds = 60;
  private const int BatchSize = 100;
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

  private static readonly string StorageBasePath =
    Environment.GetEnvironmentVariable("VMS_STORAGE_BASE_PATH")
      ?? throw new InvalidOperationException("Required environment variable 'VMS_STORAGE_BASE_PATH' is not set");

  private readonly CancellationTokenSource _cts = new();
  private Task? _task;

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _task = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        await Task.Delay(Interval, ct);
        await CleanupAsync(ct);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"ClipCleaner fatal error: {ex}");
    }
  }

  private async Task CleanupAsync(CancellationToken ct)
  {
    try
    {
      var candidates = await storage.GetUnretainedCandidatesAsync(SqlMinOlderThanSeconds, BatchSize, ct);
      if (candidates.Count == 0)
        return;

      // Group by camera to avoid redundant retention-window checks.
      var byCameraId = candidates.GroupBy(c => c.CameraId);
      foreach (var group in byCameraId)
      {
        if (ct.IsCancellationRequested) return;

        // Skip entire camera if an alarm is active right now — a RetainEvent may arrive soon.
        if (await storage.HasActiveRetentionWindowAsync(group.Key, ct))
          continue;

        var config = await configCache.GetAsync(group.Key, ct);
        var preSeconds = config?.PreRecordSeconds ?? 30;
        // A new alarm starting now would reach back pre_record_seconds into the past,
        // so only delete clips whose end_ts is older than that window.
        var safeCutoff = DateTime.UtcNow.AddSeconds(-preSeconds);

        foreach (var (clipUid, filePath, _, storageId, endTs) in group)
        {
          if (ct.IsCancellationRequested) return;
          if (endTs >= safeCutoff)
            continue;

          var basePath = string.IsNullOrEmpty(storageId) ? StorageBasePath
            : (storageListCache.TryGetRootPath(storageId) ?? StorageBasePath);

          if (!TryDeleteAssociatedFiles(basePath, filePath))
            continue;

          await storage.DeleteClipRecordAsync(clipUid, ct);
          Console.WriteLine($"ClipCleaner: deleted clip {filePath}");
        }
      }
    }
    catch (Exception ex) when (!ct.IsCancellationRequested)
    {
      Console.Error.WriteLine($"ClipCleaner cleanup cycle error: {ex}");
    }
  }

  // Deletes the video file and every sidecar file sharing the same base name
  // (e.g. .idx, .jpg, .meta) in the same directory.
  private static bool TryDeleteAssociatedFiles(string basePath, string relativeFilePath)
  {
    var fullPath = Path.GetFullPath(Path.Combine(basePath, relativeFilePath));
    var dir = Path.GetDirectoryName(fullPath);
    var nameWithoutExt = Path.GetFileNameWithoutExtension(fullPath);

    if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(nameWithoutExt))
      return true;

    if (!Directory.Exists(dir))
      return true;

    foreach (var file in Directory.GetFiles(dir, nameWithoutExt + ".*"))
    {
      try
      {
        File.Delete(file);
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"ClipCleaner: cannot delete '{file}': {ex.Message}");
        return false;
      }
    }

    return true;
  }

  public async Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    if (_task is not null)
      try { await _task; } catch (OperationCanceledException) { }
  }

  public async ValueTask DisposeAsync()
  {
    _cts.Cancel();
    if (_task is not null)
      try { await _task; } catch (OperationCanceledException) { }
    _cts.Dispose();
  }
}
