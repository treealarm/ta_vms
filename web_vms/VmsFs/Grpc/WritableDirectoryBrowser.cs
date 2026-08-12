using System.Text;
using Vms.Fs;
using web_vms.Types;

namespace VmsFs;

/// <summary>
/// Lists the directories of the node VmsFs runs on, marking which of them this process can
/// actually write into. Backs the storage-root picker in the admin UI.
/// </summary>
/// <remarks>
/// "Writable" is decided by creating and removing a probe file, not by reading permission bits:
/// the archive is written by this process, under this uid, through whatever the mount options and
/// (on Linux) ACLs happen to say, and a directory that looks writable in its metadata but rejects
/// a create is exactly the case the picker exists to keep out of the configuration.
///
/// Directories that fail the probe are still listed, marked unwritable, because the answer is
/// about the directory itself and not about what it contains: "/" is root-owned on every Linux
/// box, and the volume an operator means to record into lives inside it. Filtering them out would
/// leave that volume unreachable by browsing, which is how this started.
///
/// Paths are handled in the style of the host they belong to and are never translated between
/// POSIX and Windows separators — the value picked here is stored verbatim as a storage root and
/// travels back to this same node.
/// </remarks>
public static class WritableDirectoryBrowser
{
  /// Caps the response. A directory with more children than this is not something an operator
  /// scrolls through anyway, and the listing costs one probe file per entry.
  private const int MaxEntries = 500;

  private const string ProbePrefix = ".vms-write-probe-";

  /// Kernel-internal and container filesystems: never a place to record into, even when writable.
  /// `overlay` is here because it is a container's own root — a volume passed into a container is
  /// a separate mountinfo entry carrying the real filesystem's type, so it is still listed.
  private static readonly HashSet<string> PseudoFileSystems = new(StringComparer.Ordinal)
  {
    "autofs", "binfmt_misc", "bpf", "cgroup", "cgroup2", "configfs", "debugfs", "devpts",
    "devtmpfs", "efivarfs", "fuse.gvfsd-fuse", "fuse.portal", "fusectl", "hugetlbfs", "mqueue",
    "nsfs", "overlay", "proc", "pstore", "ramfs", "rpc_pipefs", "securityfs", "selinuxfs",
    "squashfs", "sunrpc", "sysfs", "tmpfs", "tracefs",
  };

  /// Where the OS and the container runtime keep their own mounts. A desktop or a docker host has
  /// dozens of them and not one is somewhere an archive would ever be configured, so they are left
  /// out by location rather than by type — the same directory reached by browsing is still there.
  private static readonly string[] SystemMountPrefixes =
    ["/proc", "/sys", "/dev", "/run", "/snap", "/var/snap", "/var/lib/docker", "/var/lib/kubelet"];

  public static ListDirectoriesResponse List(string? requestedPath)
  {
    var path = requestedPath ?? string.Empty;
    return string.IsNullOrWhiteSpace(path) ? ListRoots() : ListChildren(path);
  }

  // ── Roots ───────────────────────────────────────────────────────────────────────────────────

  private static ListDirectoriesResponse ListRoots()
  {
    var response = new ListDirectoriesResponse { Path = "", ParentPath = "" };

    var roots = OperatingSystem.IsWindows() ? WindowsRoots() : LinuxRoots();
    foreach (var root in roots)
    {
      response.Entries.Add(Describe(root.Path, root.Name));
      if (response.Entries.Count >= MaxEntries)
        break;
    }

    Sort(response.Entries);

    if (response.Entries.Count == 0)
      response.Message = "No volume is visible to the file service on this node";
    else if (response.Entries.All(e => !e.Writable))
      response.Message = "No volume can be written into — open one to look for a writable directory inside it";

    return response;
  }

  /// Drive letters of the types an archive can live on. Optical drives and empty slots are left
  /// out; UNC shares have no letter and are typed in by hand.
  private static IEnumerable<(string Path, string Name)> WindowsRoots()
  {
    DriveInfo[] drives;
    try { drives = DriveInfo.GetDrives(); }
    catch (Exception) { yield break; }

    foreach (var drive in drives)
    {
      bool usable;
      string name;
      try
      {
        usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network;
        name = usable && !string.IsNullOrWhiteSpace(drive.VolumeLabel)
          ? $"{drive.Name} ({drive.VolumeLabel})"
          : drive.Name;
      }
      catch (Exception)
      {
        // A drive can disappear between enumeration and inspection (removable media, a dropped
        // network mapping) — that is a "not available" answer, not a failed listing.
        continue;
      }

      if (usable)
        yield return (drive.Name, name);
    }
  }

  /// Mount points as this process sees them, from /proc/self/mountinfo rather than from
  /// DriveInfo.GetDrives(): inside a container the archive volume is usually a bind mount, which
  /// mountinfo reports (with its real filesystem type) and /etc/mtab-based views may not.
  private static IEnumerable<(string Path, string Name)> LinuxRoots() =>
    ReadMountInfo()
      .Where(m => !m.ReadOnly
               && !PseudoFileSystems.Contains(m.FsType)
               && !IsSystemMountPoint(m.MountPoint))
      .Select(m => (Path: m.MountPoint, Name: m.MountPoint));

  private static bool IsSystemMountPoint(string mountPoint) =>
    SystemMountPrefixes.Any(prefix =>
      mountPoint.Equals(prefix, StringComparison.Ordinal)
        || mountPoint.StartsWith(prefix + "/", StringComparison.Ordinal));

  private readonly record struct MountEntry(string MountPoint, string FsType, bool ReadOnly);

  /// <returns>Effective mounts, one per mount point (a later mount shadows an earlier one at the
  /// same place), in mountinfo order.</returns>
  private static List<MountEntry> ReadMountInfo()
  {
    var byMountPoint = new Dictionary<string, MountEntry>(StringComparer.Ordinal);
    var order = new List<string>();

    string[] lines;
    try { lines = File.ReadAllLines("/proc/self/mountinfo"); }
    catch (Exception) { return []; }

    foreach (var line in lines)
    {
      // "<id> <parent> <maj:min> <root> <mount point> <opts> [tags...] - <fstype> <source> <opts>"
      var separator = line.IndexOf(" - ", StringComparison.Ordinal);
      if (separator < 0)
        continue;

      var left = line[..separator].Split(' ');
      var right = line[(separator + 3)..].Split(' ');
      if (left.Length < 6 || right.Length < 1)
        continue;

      var mountPoint = UnescapeMountPath(left[4]);
      var options = left[5].Split(',');
      var entry = new MountEntry(mountPoint, right[0], options.Contains("ro"));

      if (byMountPoint.TryAdd(mountPoint, entry))
        order.Add(mountPoint);
      else
        byMountPoint[mountPoint] = entry;
    }

    return order.Select(mp => byMountPoint[mp]).ToList();
  }

  /// mountinfo escapes space, tab, newline and backslash as octal.
  private static string UnescapeMountPath(string value)
  {
    if (!value.Contains('\\'))
      return value;

    var sb = new StringBuilder(value.Length);
    for (var i = 0; i < value.Length; i++)
    {
      if (value[i] == '\\' && i + 3 < value.Length
          && TryParseOctal(value.AsSpan(i + 1, 3), out var ch))
      {
        sb.Append(ch);
        i += 3;
        continue;
      }
      sb.Append(value[i]);
    }
    return sb.ToString();
  }

  private static bool TryParseOctal(ReadOnlySpan<char> digits, out char value)
  {
    value = '\0';
    var result = 0;
    foreach (var digit in digits)
    {
      if (digit is < '0' or > '7')
        return false;
      result = result * 8 + (digit - '0');
    }
    value = (char)result;
    return true;
  }

  // ── Children ────────────────────────────────────────────────────────────────────────────────

  private static ListDirectoriesResponse ListChildren(string path)
  {
    var response = new ListDirectoriesResponse
    {
      Path = path,
      ParentPath = ParentOf(path),
      Writable = IsAbsolute(path) && IsWritable(path),
    };

    if (!IsAbsolute(path))
    {
      response.Message = "Path must be absolute";
      return response;
    }

    IEnumerable<string> children;
    try
    {
      if (!Directory.Exists(path))
      {
        response.Message = "Directory does not exist on this node";
        return response;
      }
      children = Directory.EnumerateDirectories(path);
    }
    catch (Exception ex)
    {
      response.Message = $"Directory is not readable: {ex.Message}";
      return response;
    }

    var truncated = false;
    try
    {
      foreach (var child in children)
      {
        if (response.Entries.Count >= MaxEntries)
        {
          truncated = true;
          break;
        }
        // A symlink is followed deliberately: an admin who points the archive at a symlinked
        // directory means the target, and the probe inside Describe decides whether it is usable.
        response.Entries.Add(Describe(child, LeafName(child)));
      }
    }
    catch (Exception ex)
    {
      // Enumeration can fail part-way through (a directory removed under us, a dead network
      // mount). Keep what was collected and say why the rest is missing.
      response.Message = $"Listing stopped early: {ex.Message}";
      return response;
    }

    Sort(response.Entries);

    if (truncated)
      response.Message = $"Showing the first {MaxEntries} directories";
    else if (response.Entries.Count == 0)
      response.Message = "No subdirectories";

    return response;
  }

  /// Alphabetical, so the same volume reads the same way twice. The filesystem's own order is
  /// creation order, which for a disk with hundreds of directories is no order at all.
  private static void Sort(IList<DirectoryEntry> entries)
  {
    var sorted = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    entries.Clear();
    foreach (var entry in sorted)
      entries.Add(entry);
  }

  // ── Path arithmetic (style of the path itself, never converted) ──────────────────────────────

  private static bool IsAbsolute(string path) => HostPath.IsAbsolute(path);

  private static string LeafName(string path)
  {
    var trimmed = path.TrimEnd('/', '\\');
    var cut = trimmed.LastIndexOfAny(['/', '\\']);
    return cut < 0 ? trimmed : trimmed[(cut + 1)..];
  }

  /// <returns>The directory above <paramref name="path"/>, or "" when it is a volume root and
  /// "up" therefore means the roots list.</returns>
  private static string ParentOf(string path)
  {
    if (IsVolumeRoot(path))
      return "";

    var trimmed = path.TrimEnd('/', '\\');
    var cut = trimmed.LastIndexOfAny(['/', '\\']);
    if (cut < 0)
      return "";
    // "/foo" -> "/": the separator itself is the parent, not an empty string.
    if (cut == 0)
      return trimmed[..1];

    var parent = trimmed[..cut];
    // "C:\foo" -> "C:" is not a directory; the root is "C:\".
    return IsWindowsDriveLetter(parent) ? parent + trimmed[cut] : parent;
  }

  private static bool IsVolumeRoot(string path)
  {
    var trimmed = TrimVolumeRootSlash(path);

    if (OperatingSystem.IsWindows())
      return IsWindowsDriveLetter(trimmed) || IsUncShareRoot(trimmed);

    // "/" has nowhere above it, whether or not it is listed as a root (inside a container it is
    // not — it is an overlay). Without this, "up" from it would compute "/" again.
    if (trimmed.Length == 0 || trimmed == "/")
      return true;

    // Membership in the *listed* roots, not in mountinfo at large: "up" has to land where the
    // operator can see it, and a mount that never appears in the roots list is not a stop.
    return LinuxRoots().Any(r => string.Equals(r.Path, trimmed, StringComparison.Ordinal));
  }

  private static string TrimVolumeRootSlash(string path) =>
    path.Length > 1 ? path.TrimEnd('/', '\\') : path;

  private static bool IsWindowsDriveLetter(string value) =>
    value.Length == 2 && value[1] == ':' && char.IsLetter(value[0]);

  /// "\\server\share" — the share itself, which has no parent directory.
  private static bool IsUncShareRoot(string value) =>
    value.StartsWith(@"\\", StringComparison.Ordinal)
      && value.AsSpan(2).Count('\\') == 1
      && !value.EndsWith('\\');

  // ── Probes ──────────────────────────────────────────────────────────────────────────────────

  private static DirectoryEntry Describe(string path, string name)
  {
    var entry = new DirectoryEntry { Path = path, Name = name, Writable = IsWritable(path) };
    try
    {
      // On Unix any path resolves to the volume holding it (statvfs); on Windows DriveInfo only
      // accepts a root, and a UNC path has none it will take — hence 0/0 there.
      var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(path) ?? path : path);
      entry.FreeBytes = (ulong)Math.Max(0, drive.AvailableFreeSpace);
      entry.TotalBytes = (ulong)Math.Max(0, drive.TotalSize);
    }
    catch (Exception)
    {
      // Unknown size is displayed as unknown; it must not drop the directory from the list.
    }
    return entry;
  }

  /// <summary>Creates and removes a file — the only check that answers the question actually
  /// being asked: can the recording process put an archive here.</summary>
  private static bool IsWritable(string directory)
  {
    var probe = Path.Combine(directory, ProbePrefix + Guid.NewGuid().ToString("N"));
    try
    {
      // DeleteOnClose removes the probe on both platforms, including when the process is killed
      // mid-listing on Windows (where a leftover file could not be reopened by the next attempt).
      using var stream = new FileStream(
        probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }
}
