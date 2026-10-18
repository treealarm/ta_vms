using System.Security.Cryptography;
using System.Text;

namespace web_srv;

/// <param name="ServerId">
/// MediaMTX server the session was opened on. Kept with the session rather than looked up by
/// camera when it is closed: reassigning the camera afterwards must not send the teardown to a
/// server that never had this session.
/// </param>
/// <param name="SessionPath">
/// Path (with query, if any) of the session URL MediaMTX returned — never its host.
/// </param>
/// <param name="Owner">
/// Keycloak preferred_username of whoever opened it, empty when authentication is disabled.
/// </param>
public sealed record WhepSession(string CameraId, string ServerId, string Owner, string SessionPath);

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

  public string Seal(WhepSession session, DateTime expiresUtc)
  {
    var payload = Encoding.UTF8.GetBytes(string.Join('\n',
      new DateTimeOffset(expiresUtc, TimeSpan.Zero).ToUnixTimeSeconds().ToString(),
      session.Owner,
      session.CameraId,
      session.ServerId,
      session.SessionPath));

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
  public bool TryOpen(string token, out WhepSession session)
  {
    session = null!;

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

    var fields = Encoding.UTF8.GetString(payload).Split('\n', 5);
    if (fields.Length != 5)
      return false;

    if (!long.TryParse(fields[0], out var expiresUnix)
      || DateTimeOffset.FromUnixTimeSeconds(expiresUnix) < DateTimeOffset.UtcNow)
      return false;

    // Owner may legitimately be empty (no Keycloak); the rest identify the session and may not be.
    if (fields[2].Length == 0 || fields[3].Length == 0 || fields[4].Length == 0)
      return false;

    session = new WhepSession(fields[2], fields[3], fields[1], fields[4]);
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
