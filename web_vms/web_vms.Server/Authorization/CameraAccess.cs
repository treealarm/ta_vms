namespace web_vms.Authorization;

/// <summary>What a request wants to do with a camera. Mirrors CameraPermissionFlagsDto.</summary>
public enum CameraAction
{
  ViewLive,
  ViewArchive,
  Ptz,
  /// <summary>Arming, recording overrides, manual retention, relay outputs.</summary>
  Control,
  /// <summary>Downloading a clip as a file.</summary>
  Export,
}

/// <summary>
/// The set of cameras an action is permitted on. <see cref="All"/> models a blanket grant without
/// ever materialising the camera list, so a permission check never needs to know how many cameras
/// exist.
/// </summary>
public sealed class CameraScope
{
  // Declared first: static initializers run in source order, and both singletons below need it.
  private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.Ordinal);

  public static readonly CameraScope Everything = new(all: true, EmptyIds);
  public static readonly CameraScope Nothing = new(all: false, EmptyIds);

  private CameraScope(bool all, IReadOnlySet<string> ids)
  {
    All = all;
    Ids = ids;
  }

  public static CameraScope Of(IReadOnlySet<string> ids) => ids.Count == 0 ? Nothing : new(false, ids);

  public bool All { get; }
  /// <summary>Meaningless when <see cref="All"/> is true — ask <see cref="Contains"/> instead.</summary>
  public IReadOnlySet<string> Ids { get; }

  public bool IsEmpty => !All && Ids.Count == 0;

  public bool Contains(string cameraId) => All || Ids.Contains(cameraId);

  public IEnumerable<T> Filter<T>(IEnumerable<T> items, Func<T, string> cameraIdOf) =>
    All ? items : items.Where(i => Ids.Contains(cameraIdOf(i)));
}
