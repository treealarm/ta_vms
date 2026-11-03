using System.Collections.Generic;

namespace VmsMap.Demo
{
  /// What an install did: how many rows of each kind it made, changed or removed, and the cameras
  /// the deployment does not have yet. A camera missing today is placed by reinstalling once it
  /// exists, so it is reported rather than treated as an error.
  public sealed class DemoReport
  {
    public string Key { get; init; } = string.Empty;
    public int Version { get; init; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public List<string> SkippedCameras { get; } = [];
  }

  /// Whether a world is installed, and at which version.
  public sealed class DemoStatus
  {
    public string Key { get; init; } = string.Empty;
    public bool Installed { get; init; }
    public int Version { get; init; }
    public int Entities { get; init; }
  }
}
