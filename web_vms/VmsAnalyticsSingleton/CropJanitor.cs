using Microsoft.EntityFrameworkCore;

namespace VmsAnalyticsSingleton;

// Ring-style cleanup for detection rows and their crop files on the storage volumes: everything
// older than ANALYTICS_CROP_RETENTION_DAYS goes, and if the crops still exceed
// ANALYTICS_CROP_MAX_BYTES the oldest are deleted until under the cap. Both variables are
// genuinely optional tunables (defaults documented below) — not required env vars.
public sealed class CropJanitor(
  IDbContextFactory<AnalyticsDbContext> dbFactory,
  CameraStorageResolver storageResolver,
  ILogger<CropJanitor> logger) : BackgroundService
{
  private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

  // Default 7 days when unset.
  private static readonly int RetentionDays = OptionalInt("ANALYTICS_CROP_RETENTION_DAYS", 7);
  // Default 1 GiB when unset.
  private static readonly long MaxBytes = OptionalLong("ANALYTICS_CROP_MAX_BYTES", 1024L * 1024 * 1024);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await SweepAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "CropJanitor sweep failed");
      }
      await Task.Delay(SweepInterval, stoppingToken);
    }
  }

  private async Task SweepAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var deletedFiles = 0;
    long freedBytes = 0;

    // 1. Age limit: rows (and their crop files) older than the retention window. A row whose
    // crop file can't currently be resolved (VmsCfg down, storage list unavailable) is kept for
    // the next sweep — deleting it now would orphan the file on disk with nothing left to
    // reference (and thus clean) it.
    var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
    var expired = await db.Detections.Where(d => d.DetectedAt < cutoff).ToListAsync(ct);
    var removable = new List<DbDetection>();
    foreach (var row in expired)
    {
      if (row.CropPath is not null)
      {
        var path = await ResolveCropPathAsync(row, ct);
        if (path is null)
          continue; // storage root unknown right now — retry next sweep
        freedBytes += DeleteCropFile(path, () => deletedFiles++);
      }
      removable.Add(row);
    }
    db.Detections.RemoveRange(removable);
    await db.SaveChangesAsync(ct);

    // 2. Size ring: sum crop file sizes newest-first; everything past the cap is deleted
    // oldest-first (only the crop file — the detection row keeps its metadata).
    var withCrops = await db.Detections
      .Where(d => d.CropPath != null)
      .OrderByDescending(d => d.DetectedAt)
      .ToListAsync(ct);

    long total = 0;
    foreach (var row in withCrops)
    {
      var path = await ResolveCropPathAsync(row, ct);
      if (path is null)
        continue; // storage root unknown right now — retry next sweep
      var size = File.Exists(path) ? new FileInfo(path).Length : 0;
      total += size;
      if (total > MaxBytes)
      {
        freedBytes += DeleteCropFile(path, () => deletedFiles++);
        row.StorageId = null;
        row.CropPath = null;
      }
    }
    await db.SaveChangesAsync(ct);

    if (deletedFiles > 0)
      logger.LogInformation("CropJanitor: deleted {Files} crop files, freed {Mb:F1} MB (expired rows: {Rows})",
        deletedFiles, freedBytes / 1024.0 / 1024.0, expired.Count);
  }

  private long DeleteCropFile(string path, Action onDeleted)
  {
    try
    {
      if (!File.Exists(path)) return 0;
      var size = new FileInfo(path).Length;
      File.Delete(path);
      onDeleted();
      PruneEmptyDirs(Path.GetDirectoryName(path));
      return size;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "CropJanitor: failed to delete '{Path}'", path);
      return 0;
    }
  }

  private async Task<string?> ResolveCropPathAsync(DbDetection row, CancellationToken ct)
  {
    if (row.StorageId is null || row.CropPath is null) return null;
    var root = await storageResolver.GetRootPathAsync(row.StorageId, ct);
    return root is null ? null : Path.Combine(root, row.CropPath);
  }

  // Walks up from a dated leaf dir (crops/{camera}/{yyyy/MM/dd/HH}) removing empty levels, but
  // never above the "crops" segment itself.
  private static void PruneEmptyDirs(string? dir)
  {
    while (dir is not null
           && Path.GetFileName(dir) != "crops"
           && Directory.Exists(dir)
           && !Directory.EnumerateFileSystemEntries(dir).Any())
    {
      try { Directory.Delete(dir); } catch { return; }
      dir = Path.GetDirectoryName(dir);
    }
  }

  private static int OptionalInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

  private static long OptionalLong(string name, long fallback) =>
    long.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;
}
