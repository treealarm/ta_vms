using System.Security.Cryptography;
using System.Text;

namespace VmsCfg.Demo;

/// The ids the demo's cameras are configured under.
///
/// A demo camera has a name (`sim-master`) and an id; the wall has to know the id, and the world
/// that places the cameras derives it the same way from the name. VmsMap and VmsCfg are separate
/// processes with no shared project for it, so the namespace and key below are a contract between
/// them — the same one VmsMap's DemoIds implements, and the tests of both pin the result
/// (`94681efe-57da-5093-b420-1bed3b28dae8` for `sim-master`).
public static class DemoCameraIds
{
  // A fixed namespace of our own; changing it renames every camera of every installed world.
  private static readonly Guid Namespace = new("0f3c1a52-6f5b-4a0e-9a19-3a8a5a4b7c11");

  public static string For(string name) => Compute($"camera/{name}");

  // RFC 4122 version 5: SHA-1 of the namespace and the name, with the version and variant bits
  // set. Written out rather than taken from a package because this is ten lines and the project
  // has no UUID library.
  private static string Compute(string key)
  {
    var namespaceBytes = Namespace.ToByteArray(bigEndian: true);
    var nameBytes = Encoding.UTF8.GetBytes(key);
    var input = new byte[namespaceBytes.Length + nameBytes.Length];
    namespaceBytes.CopyTo(input, 0);
    nameBytes.CopyTo(input, namespaceBytes.Length);

    var hash = SHA1.HashData(input);
    hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
    hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
    return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
  }
}
