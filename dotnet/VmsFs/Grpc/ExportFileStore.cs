using Google.Protobuf.WellKnownTypes;
using Vms.Fs;

namespace VmsFs;

/// <summary>
/// mp4 files produced by a camera-side import with <c>export_to_file</c>, laid out as
/// <c>&lt;VMS_EXPORT_BASE_PATH&gt;/&lt;camera_id&gt;/&lt;job_id&gt;.mp4</c>.
/// </summary>
/// <remarks>
/// There is no database table: the directory itself is the state. That keeps exports discoverable
/// across a video_srv restart (which drops the in-memory job registry) and makes the camera id
/// available for authorization straight from the path, with no lookup.
/// </remarks>
public sealed class ExportFileStore
{
  private const string ExportExtension = ".mp4";

  private readonly string _basePath;
  private readonly ArchiveFileStorage _paths;
  private readonly ILogger<ExportFileStore> _logger;

  public ExportFileStore(ILogger<ExportFileStore> logger)
  {
    _basePath = Environment.GetEnvironmentVariable("VMS_EXPORT_BASE_PATH")
      ?? throw new InvalidOperationException("Required environment variable 'VMS_EXPORT_BASE_PATH' is not set");
    _paths = new ArchiveFileStorage(_basePath);
    _logger = logger;
  }

  public string BasePath => _basePath;

  /// <summary>Absolute path, or null when the relative path escapes the export root.</summary>
  public string? Resolve(string relPath) => _paths.ResolveRelativePath(relPath);

  public List<ExportFileInfo> List(string cameraId)
  {
    var result = new List<ExportFileInfo>();
    if (!Directory.Exists(_basePath))
      return result;

    // One level of camera directories; anything else in the export root is ignored rather than
    // reported, so a stray file cannot break the listing.
    foreach (var cameraDir in Directory.EnumerateDirectories(_basePath))
    {
      var dirCamera = Path.GetFileName(cameraDir);
      if (!string.IsNullOrEmpty(cameraId) && dirCamera != cameraId)
        continue;

      foreach (var file in Directory.EnumerateFiles(cameraDir, "*" + ExportExtension))
      {
        FileInfo info;
        try { info = new FileInfo(file); }
        catch (Exception ex)
        {
          _logger.LogDebug(ex, "Skipping unreadable export file {File}", file);
          continue;
        }
        result.Add(new ExportFileInfo
        {
          RelPath = Path.Combine(dirCamera, info.Name),
          CameraId = dirCamera,
          SizeBytes = (ulong)info.Length,
          CreatedTs = Timestamp.FromDateTime(info.LastWriteTimeUtc),
        });
      }
    }

    return result.OrderByDescending(f => f.CreatedTs).ToList();
  }

  /// <summary>Deletes exports last written before <paramref name="olderThanUtc"/>. Returns the count removed.</summary>
  public int DeleteOlderThan(DateTime olderThanUtc)
  {
    if (!Directory.Exists(_basePath))
      return 0;

    var removed = 0;
    foreach (var file in Directory.EnumerateFiles(_basePath, "*" + ExportExtension, SearchOption.AllDirectories))
    {
      try
      {
        if (File.GetLastWriteTimeUtc(file) >= olderThanUtc)
          continue;
        File.Delete(file);
        removed++;
        _logger.LogInformation("Deleted expired export {File}", file);
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to delete expired export {File}", file);
      }
    }
    return removed;
  }
}
