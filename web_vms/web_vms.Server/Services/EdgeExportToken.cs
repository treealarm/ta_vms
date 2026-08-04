using System.Security.Cryptography;
using System.Text;

namespace web_vms;

/// <summary>
/// Seals "this browser may read this exported file for the next few minutes" into a URL parameter.
/// </summary>
/// <remarks>
/// A media element cannot carry an Authorization header, so an export can only be played directly by
/// putting the grant in the URL. The alternative — fetching the whole file and handing the element a
/// blob — costs a full download before the first frame and all of it in memory.
///
/// Shares <c>VMS_WHEP_SESSION_KEY</c> with <see cref="WhepSessionToken"/> on purpose: both are
/// short-lived, browser-facing grants issued by this gateway, and one secret to rotate is better
/// than two. The payload starts with a type marker so a token minted here can never be opened as a
/// WHEP session, or the other way round.
/// </remarks>
public sealed class EdgeExportToken
{
  private const string TokenType = "edge-export";
  private const int NonceSize = 12;
  private const int TagSize = 16;
  private const int KeySize = 32;

  /// <summary>Long enough to start playback and seek around, short enough that a leaked URL dies quickly.</summary>
  public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

  private readonly byte[] _key;

  public EdgeExportToken(string base64Key)
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
        $"Environment variable 'VMS_WHEP_SESSION_KEY' must decode to {KeySize} bytes, got {key.Length}");
    _key = key;
  }

  public string Seal(string relPath, string owner, DateTime expiresUtc)
  {
    var payload = Encoding.UTF8.GetBytes(string.Join('\n',
      TokenType,
      new DateTimeOffset(expiresUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(),
      owner,
      relPath));

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
  /// True only for a live token this key sealed for exactly <paramref name="relPath"/>. Malformed,
  /// tampered, expired and "for another file" are indistinguishable from the outside, so a token
  /// cannot be probed for which it is.
  /// </summary>
  public bool IsValidFor(string token, string relPath)
  {
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
    if (fields.Length != 4 || fields[0] != TokenType)
      return false;
    if (!long.TryParse(fields[1], out var expiresUnix)
      || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < DateTimeOffset.UtcNow)
      return false;

    return string.Equals(fields[3], relPath, StringComparison.Ordinal);
  }

  private static string ToBase64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

  private static bool TryFromBase64Url(string value, out byte[] bytes)
  {
    bytes = [];
    if (string.IsNullOrEmpty(value))
      return false;

    var padded = value.Replace('-', '+').Replace('_', '/');
    padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
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
