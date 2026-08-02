using System.Security.Cryptography;
using System.Text;

namespace web_vms;

/// <summary>
/// Seals a WHEP session (its camera, the session path MediaMTX handed out, and the owner) into the
/// opaque token the browser gets back, and opens it again when the browser asks to close it.
///
/// The token carries its own contents instead of being a key into a per-process table, so any
/// replica can close a session opened by any other one — a table would make the gateway require
/// session affinity, which is the one thing keeping it from scaling horizontally.
///
/// It deliberately does <b>not</b> carry a host: the MediaMTX address is always re-resolved from
/// the camera's configuration when the session is closed (see <see cref="WhepProxyService"/>). A
/// token therefore cannot name a destination, no matter what it contains, which keeps "the gateway
/// only ever calls a MediaMTX it was configured to call" true by construction rather than by trust
/// in the sealing key. AES-GCM on top of that authenticates the camera and owner and keeps the
/// path opaque to the browser.
/// </summary>
public sealed class WhepSessionToken
{
  private const int NonceSize = 12;   // AES-GCM standard nonce
  private const int TagSize   = 16;
  private const int KeySize   = 32;   // AES-256

  private readonly byte[] _key;

  private WhepSessionToken(byte[] key) => _key = key;

  /// <exception cref="InvalidOperationException">The key is not base64 of exactly 32 bytes.</exception>
  public static WhepSessionToken FromBase64Key(string base64Key)
  {
    byte[] key;
    try
    {
      key = Convert.FromBase64String(base64Key);
    }
    catch (FormatException)
    {
      throw new InvalidOperationException(
        "Environment variable 'VMS_WHEP_SESSION_KEY' must be base64 of 32 random bytes "
        + "(generate one with: openssl rand -base64 32)");
    }

    if (key.Length != KeySize)
      throw new InvalidOperationException(
        $"Environment variable 'VMS_WHEP_SESSION_KEY' must decode to {KeySize} bytes, got {key.Length} "
        + "(generate one with: openssl rand -base64 32)");

    return new WhepSessionToken(key);
  }

  /// <param name="sessionPath">
  /// Path (with query, if any) of the session URL MediaMTX returned — never its host.
  /// </param>
  public string Seal(string cameraId, string sessionPath, string owner, DateTime expiresUtc)
  {
    var payload = Encoding.UTF8.GetBytes(
      $"{new DateTimeOffset(expiresUtc, TimeSpan.Zero).ToUnixTimeSeconds()}\n{owner}\n{cameraId}\n{sessionPath}");

    var token = new byte[NonceSize + TagSize + payload.Length];
    var nonce = token.AsSpan(0, NonceSize);
    var tag = token.AsSpan(NonceSize, TagSize);
    var ciphertext = token.AsSpan(NonceSize + TagSize);

    RandomNumberGenerator.Fill(nonce);
    using var aes = new AesGcm(_key, TagSize);
    aes.Encrypt(nonce, payload, ciphertext, tag);

    return ToBase64Url(token);
  }

  /// <summary>
  /// False for anything that is not a live token this key sealed — malformed, tampered with, or
  /// expired all look the same, so a token cannot be probed for which of those it is.
  /// </summary>
  public bool TryOpen(string token, out string cameraId, out string sessionPath, out string owner)
  {
    cameraId = string.Empty;
    sessionPath = string.Empty;
    owner = string.Empty;

    if (!TryFromBase64Url(token, out var raw) || raw.Length <= NonceSize + TagSize)
      return false;

    var payload = new byte[raw.Length - NonceSize - TagSize];
    try
    {
      using var aes = new AesGcm(_key, TagSize);
      aes.Decrypt(
        raw.AsSpan(0, NonceSize),
        raw.AsSpan(NonceSize + TagSize),
        raw.AsSpan(NonceSize, TagSize),
        payload);
    }
    catch (CryptographicException)
    {
      return false;
    }

    var fields = Encoding.UTF8.GetString(payload).Split('\n', 4);
    if (fields.Length != 4)
      return false;

    if (!long.TryParse(fields[0], out var expiresUnix)
      || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < DateTimeOffset.UtcNow)
      return false;

    if (fields[2].Length == 0 || fields[3].Length == 0)
      return false;

    owner = fields[1];
    cameraId = fields[2];
    sessionPath = fields[3];
    return true;
  }

  // The token travels as a URL path segment, so '+' and '/' cannot be used and padding is noise.
  private static string ToBase64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static bool TryFromBase64Url(string value, out byte[] bytes)
  {
    bytes = [];
    if (string.IsNullOrEmpty(value))
      return false;

    var padded = value.Replace('-', '+').Replace('_', '/');
    padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };

    try
    {
      bytes = Convert.FromBase64String(padded);
      return true;
    }
    catch (FormatException)
    {
      return false;
    }
  }
}
