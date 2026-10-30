using System.Security.Cryptography;
using System.Text;

namespace VmsMap.Domain
{
  /// <summary>
  /// A stable object id derived from a name and a version: the same camera always gets the same
  /// tree node, across restarts and replicas. The adapter (M3) uses it to place a camera under
  /// its server node before an administrator moves it into a space.
  /// </summary>
  public static class ObjectIdGenerator
  {
    public static string GenerateObjectId(string input, string version)
    {
      input ??= string.Empty;
      version ??= string.Empty;

      string combined = input + version;

      // MD5 gives the 16 bytes a Guid needs; this is an identity, not a signature.
      using var md5 = MD5.Create();
      byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(combined));

      return new Guid(hash).ToString();
    }
  }
}
