using System.Security.Cryptography;
using System.Text;

namespace web_vms;

/// <summary>
/// Seals a WHEP session (the real MediaMTX session URL plus its owner) into the opaque token the
/// browser gets back, and opens it again when the browser asks to close the session.
///
/// The token carries its own contents instead of being a key into a per-process table, so any
/// replica can close a session opened by any other one — a table would make the gateway require
/// session affinity, which is the one thing keeping it from scaling horizontally.
///
/// AES-GCM rather than a plain signature: the sealed payload names an internal MediaMTX host and
/// port, and there is no reason to hand that to the browser. Authentication comes with it — a
/// token that opens is proof the URL inside was produced by <see cref="WhepProxyService"/> itself,
/// which is exactly the anti-SSRF property the old lookup table provided by construction.
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

  public string Seal(Uri sessionUri, string owner, DateTime expiresUtc)
  {
    var payload = Encoding.UTF8.GetBytes(
      $"{new DateTimeOffset(expiresUtc, TimeSpan.Zero).ToUnixTimeSeconds()}\n{owner}\n{sessionUri.AbsoluteUri}");

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
  public bool TryOpen(string token, out Uri sessionUri, out string owner)
  {
    sessionUri = null!;
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

    var text = Encoding.UTF8.GetString(payload);
    var firstBreak = text.IndexOf('\n');
    if (firstBreak < 0) return false;
    var secondBreak = text.IndexOf('\n', firstBreak + 1);
    if (secondBreak < 0) return false;

    if (!long.TryParse(text.AsSpan(0, firstBreak), out var expiresUnix)
      || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < DateTimeOffset.UtcNow)
      return false;

    if (!Uri.TryCreate(text[(secondBreak + 1)..], UriKind.Absolute, out var uri))
      return false;

    sessionUri = uri;
    owner = text[(firstBreak + 1)..secondBreak];
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
