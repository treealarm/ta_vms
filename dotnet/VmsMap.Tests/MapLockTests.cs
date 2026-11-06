using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VmsMap.Tests;

// The lock order is a correctness rule, not a convention: one handler that takes the alarm lock
// before the camera-state lock deadlocks against every other writer. It is enforced by reading the
// source, because a test that merely ran two writers would pass until the unlucky interleaving.
public class MapLockTests
{
  private static IEnumerable<string> Sources()
  {
    var root = AppContext.BaseDirectory;
    while (root is not null && !Directory.Exists(Path.Combine(root, "dotnet", "VmsMap")))
      root = Path.GetDirectoryName(root);

    if (root is null)
      throw new InvalidOperationException("Could not find the repository root from " + AppContext.BaseDirectory);

    return Directory
      .EnumerateFiles(Path.Combine(root, "dotnet", "VmsMap"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
  }

  [Fact]
  public void AdvisoryLocksAreOnlyTakenThroughMapLocks()
  {
    var offenders = Sources()
      .Where(f => File.ReadAllText(f).Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
      .Select(Path.GetFileName)
      .Where(name => name != "MapLocks.cs")
      .ToList();

    Assert.Empty(offenders);
  }

  [Fact]
  public void AFileThatTakesBothTakesTheCameraStateLockFirst()
  {
    var offenders = new List<string>();

    foreach (var file in Sources())
    {
      var text = File.ReadAllText(file);
      var camera = text.IndexOf("AcquireCameraStateAsync", StringComparison.Ordinal);
      var alarm = text.IndexOf("AcquireAlarmAsync", StringComparison.Ordinal);
      if (camera >= 0 && alarm >= 0 && alarm < camera)
        offenders.Add(Path.GetFileName(file)!);
    }

    Assert.Empty(offenders);
  }
}
