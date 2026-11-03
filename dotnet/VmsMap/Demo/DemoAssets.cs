using System;
using System.IO;
using System.Linq;

namespace VmsMap.Demo
{
  /// The pictures the world brings. They are embedded in the assembly rather than read from disk:
  /// the service is replicable and an image has to be there whichever instance installs the demo.
  public static class DemoAssets
  {
    public static byte[] Read(string asset)
    {
      var assembly = typeof(DemoAssets).Assembly;
      var suffix = $".Demo.Assets.{asset.Replace('/', '.')}";
      var name = assembly.GetManifestResourceNames()
        .FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));

      if (name is null)
      {
        throw new InvalidOperationException(
          $"The demo asset '{asset}' is not embedded in the assembly");
      }

      using var stream = assembly.GetManifestResourceStream(name)!;
      using var buffer = new MemoryStream();
      stream.CopyTo(buffer);
      return buffer.ToArray();
    }
  }
}
