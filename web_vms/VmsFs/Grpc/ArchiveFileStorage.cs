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
    return IsWithinBase(combined) ? combined : null;
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
