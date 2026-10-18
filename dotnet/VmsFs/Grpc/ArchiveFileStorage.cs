namespace VmsFs;

public sealed class ArchiveFileStorage(string basePath)
{
  private readonly string _basePath = Path.GetFullPath(basePath);

  public string? ResolveRelativePath(string relativePath)
  {
    if (string.IsNullOrEmpty(relativePath))
      return null;
    if (relativePath.Contains(".."))
      return null;
    if (Path.IsPathRooted(relativePath))
      return null;

    var combined = Path.GetFullPath(Path.Combine(_basePath, relativePath));
    if (!IsWithinBase(combined))
      return null;

    // Path.GetFullPath resolves ".." but not symlinks. Walk each component and
    // resolve any symlink before re-checking, so a symlink inside the archive
    // root cannot redirect a read to a path outside it.
    var canonical = ResolveSymlinks(combined);
    if (canonical == null || !IsWithinBase(canonical))
      return null;

    return combined;
  }

  // Walks path component-by-component and follows any symlink at each level.
  // Returns null on I/O error so the caller rejects the path.
  private static string? ResolveSymlinks(string path)
  {
    try
    {
      var parts = path.Split(Path.DirectorySeparatorChar,
        StringSplitOptions.RemoveEmptyEntries);
      var current = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();

      foreach (var part in parts)
      {
        current = Path.Combine(current, part);
        string? target = null;
        try
        {
          // Try FileInfo first (works for both files and dir-symlinks on Linux),
          // fall back to DirectoryInfo for directory entries that are not links
          // but whose ResolveLinkTarget contract differs by runtime/platform.
          target = new FileInfo(current).ResolveLinkTarget(true)?.FullName
                ?? new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName;
        }
        catch { /* component may not exist yet; leave current unchanged */ }

        if (target is not null)
          current = target;
      }

      return current;
    }
    catch
    {
      return null;
    }
  }

  private bool IsWithinBase(string candidate)
  {
    if (candidate.Length < _basePath.Length)
      return false;
    if (!candidate.StartsWith(_basePath, StringComparison.Ordinal))
      return false;
    if (candidate.Length == _basePath.Length)
      return true;
    return candidate[_basePath.Length] == Path.DirectorySeparatorChar;
  }
}
