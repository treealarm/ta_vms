using System;
using System.Security.Cryptography;
using System.Text;

namespace VmsMap.Demo
{
  /// The ids of the demo world: a UUIDv5 of the world key and the entity's logical key, so the
  /// same `diagram/rack-a-01` is the same node on every install of every deployment. A space is
  /// the exception — the server assigns its id — and the registry records what it got.
  public static class DemoIds
  {
    // A fixed namespace of our own; changing it renames every node of every installed world.
    private static readonly Guid Namespace = new("0f3c1a52-6f5b-4a0e-9a19-3a8a5a4b7c11");

    public static Guid For(string key)
    {
      var namespaceBytes = Namespace.ToByteArray(bigEndian: true);
      var nameBytes = Encoding.UTF8.GetBytes(key);
      var input = new byte[namespaceBytes.Length + nameBytes.Length];
      namespaceBytes.CopyTo(input, 0);
      nameBytes.CopyTo(input, namespaceBytes.Length);

      var hash = SHA1.HashData(input);
      hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
      hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
      return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    public static string Id(string key) => For(key).ToString();

    /// The camera id a demo camera's name stands for in VmsCfg. Camera ids are UUIDs; the name
    /// (`sim-master`) is what the world's model and its report speak, and this is the one place the
    /// two are tied together.
    public static string Camera(string name) => Id($"camera/{name}");
  }
}
