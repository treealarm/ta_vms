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

  [Fact]
  public void TheDemoSeederTakesTheMapLocksBeforeItWrites()
  {
    // The install writes the same object, state and alarm rows as the device path, and that path
    // takes both locks before its own writes. The seeder used to take them only when it reached
    // DeviceDiagramService.AttachAsync, by which time its transaction already held rows — the
    // reverse order, which deadlocked a bench install against the connectivity poller.
    var file = Sources().Single(f => Path.GetFileName(f) == "DemoSeeder.cs");
    var text = File.ReadAllText(file);

    // The helper itself must take the map's locks, not only the demo one: the install writes
    // object rows, so a demo lock alone leaves the reverse order in place.
    var helper = text.IndexOf("private async Task LockAsync", StringComparison.Ordinal);
    Assert.True(helper >= 0, "DemoSeeder.LockAsync not found");
    Assert.Contains("AcquireAllAsync", text[helper..], StringComparison.Ordinal);

    // And within the install, the call must come before the first map row is written.
    var start = text.IndexOf("public async Task<DemoReport> InstallAsync", StringComparison.Ordinal);
    var end = text.IndexOf("public async Task<DemoReport> RemoveAsync", StringComparison.Ordinal);
    Assert.True(start >= 0 && end > start, "DemoSeeder.InstallAsync not found");

    var install = text[start..end];
    var locks = install.IndexOf("LockAsync", StringComparison.Ordinal);
    var firstWrite = install.IndexOf("UpsertSpaceAsync", StringComparison.Ordinal);
    Assert.True(firstWrite >= 0, "DemoSeeder no longer writes spaces; update this test");
    Assert.True(locks >= 0 && locks < firstWrite,
      "DemoSeeder must take the map's locks before writing map rows");
  }
}
