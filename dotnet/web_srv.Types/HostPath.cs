namespace web_srv.Types;

/// <summary>
/// Recognizes an absolute filesystem path of a recording node, in either OS style.
/// </summary>
/// <remarks>
/// A path configured here belongs to the node that will record into it, not to the service that
/// validates or stores it: VmsCfg and the gateway routinely run on Linux while the media node runs
/// on Windows, and the other way round. So the rule is deliberately style-agnostic and does no
/// normalization at all — separators are never converted, nothing is resolved, the string is stored
/// and handed back exactly as typed. Whether the directory exists and can be written is answered by
/// the file service on that node (see <c>WritableDirectoryBrowser</c> in VmsFs), not here.
/// </remarks>
public static class HostPath
{
  /// <summary>POSIX ("/srv/video"), Windows drive ("D:\video", "D:/video") or UNC ("\\nas\video").</summary>
  public static bool IsAbsolute(string? path)
  {
    if (string.IsNullOrWhiteSpace(path))
      return false;

    return path.StartsWith('/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || IsDriveRooted(path);
  }

  private static bool IsDriveRooted(string path) =>
    path.Length >= 3
      && char.IsLetter(path[0])
      && path[1] == ':'
      && path[2] is '\\' or '/';
}
