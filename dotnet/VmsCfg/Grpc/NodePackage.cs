using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VmsCfg;

/// <summary>
/// The package that joins a machine to this core as a node: a gzipped tar that TaVmsHost (Windows)
/// and scripts/distributed.sh (Linux) read in ../ta_install.
/// </summary>
/// <remarks>
/// <code>
///   shared.env           CORE_ADDRESS, CONSUL_ENCRYPT, DAPR_TRUST_ANCHORS (the core's file, as it is)
///   core.env             what the node takes from this core: credentials, ports and service addresses
///   name                 the node's name
///   tokens/&lt;app&gt;.token   a Dapr Sentry token for each of the node's app ids, and for nothing else
///   tls/ca.crt           the core's TLS certificate authority
///   tls/server.crt       the node's certificate for its address, issued by it and followed by it
///   tls/server.key       that certificate's key
/// </code>
/// It is the node's credential: anything it is handed to can join and read the core's passwords.
/// Text is written with '\n' and no byte-order mark, since on Linux sed and compose read it.
/// </remarks>
public static partial class NodePackage
{
  /// The node's services, whose app ids carry its name. Windows' services.json and
  /// docker-compose.distributed.yml give a node exactly these; a token missing here is a sidecar
  /// that cannot get a certificate.
  public static readonly string[] AppIdPrefixes = ["vms", "vmsfs", "vmsonvif", "vmsonvif-actors", "vmslogger", "web-vms"];

  public static IEnumerable<string> AppIds(string node) => AppIdPrefixes.Select(p => $"{p}-{node}");

  public static (string ServerId, string FsId, string OnvifId, string LoggerId) Pair(string node) =>
    ($"vms-{node}", $"vmsfs-{node}", $"vmsonvif-{node}", $"vmslogger-{node}");

  [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
  private static partial Regex NodeName();

  /// Null when the name can name a node, the reason otherwise. "core" is taken by the core's own
  /// files; the length keeps the app ids within what Consul and Dapr take.
  public static string? NameProblem(string name) =>
    name.Length > 40 || !NodeName().IsMatch(name) || name == "core"
      ? $"'{name}' cannot name a node: up to 40 lower-case letters, digits and '-', and not 'core'"
      : null;

  /// Null when the address is one another machine can reach a node at, the reason otherwise.
  public static string? AddressProblem(string address) =>
    IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
      && !IPAddress.IsLoopback(ip)
      ? null
      : $"'{address}' is not an IPv4 address other machines can reach";

  // What a node takes from its core, the same list distributed.sh's CORE_KEYS names: everything else
  // is the same in every release, or belongs to the machine, and a Windows core's would be Windows
  // paths, of no use to a node on Linux.
  [GeneratedRegex(@"^(POSTGRES_(PORT|USER|PASSWORD)|POSTGRES_[A-Z0-9_]+_DB|RABBITMQ_(PORT|DEFAULT_USER|DEFAULT_PASS)|VMS_MEDIAMTX_(PUBLISH|READ)_(USER|PASS)|KEYCLOAK_(PORT|ADMIN_USER|ADMIN_PASSWORD|REALM_NAME|CLIENT_ID)|VMS_WHEP_SESSION_KEY)$")]
  private static partial Regex CoreKeys();

  // A node cannot reach the core's services without these; its web logs users in at the core's
  // Keycloak and seals WHEP sessions as every other web does.
  private static readonly string[] RequiredCoreKeys =
  [
    "POSTGRES_PORT", "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_STATE_DB", "POSTGRES_FS_DB",
    "RABBITMQ_PORT", "RABBITMQ_DEFAULT_USER", "RABBITMQ_DEFAULT_PASS",
    "KEYCLOAK_PORT", "KEYCLOAK_ADMIN_USER", "KEYCLOAK_ADMIN_PASSWORD", "KEYCLOAK_REALM_NAME", "KEYCLOAK_CLIENT_ID",
    "VMS_WHEP_SESSION_KEY",
  ];

  private const string SentryId = "spiffe://localhost/ns/default/dapr-sentry";
  private const string KeyId = "tavms";

  /// The core's TLS certificate authority (ta_install: tls/ca.crt and core/tls-ca.key), PEM.
  public sealed record TlsAuthority(string CertPem, string KeyPem);

  /// <param name="address">The node's own address, which its certificate is for.</param>
  /// <param name="sharedEnv">The core's shared.env, copied as it is.</param>
  /// <param name="env">This process's environment: the core's own values.</param>
  public static byte[] Build(string name, string address, string signingKeyPem, TlsAuthority tls, string sharedEnv,
    IReadOnlyDictionary<string, string> env, DateTimeOffset now)
  {
    if (NameProblem(name) is { } problem)
      throw new ArgumentException(problem, nameof(name));
    if (AddressProblem(address) is { } addressProblem)
      throw new ArgumentException(addressProblem, nameof(address));
    var coreAddress = ReadValue(sharedEnv, "CORE_ADDRESS")
      ?? throw new InvalidOperationException("shared.env names no CORE_ADDRESS");

    var (serverCert, serverKey) = ServerCertificate(tls, address, now);
    var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
    {
      ["shared.env"] = sharedEnv,
      ["core.env"] = CoreEnv(env, coreAddress),
      ["name"] = name + "\n",
      ["tls/ca.crt"] = tls.CertPem,
      ["tls/server.crt"] = serverCert,
      ["tls/server.key"] = serverKey,
    };
    using (var key = ECDsa.Create())
    {
      key.ImportFromPem(signingKeyPem);
      foreach (var appId in AppIds(name))
        files[$"tokens/{appId}.token"] = Token(key, $"spiffe://public/ns/default/{appId}", now);
    }

    using var buffer = new MemoryStream();
    using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
    using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
    {
      foreach (var (entry, content) in files)
      {
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry)
        {
          // 0600: on Linux the files are a node's credentials.
          Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
          ModificationTime = now,
          DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"))),
        });
      }
    }
    return buffer.ToArray();
  }

  // What the core's own certificate is (ta_install: scripts/distributed.sh issue_server_cert and
  // TaVmsHost's Pki.IssueServerCertificate), for the node's address: EC P-256 with its key in PKCS#8,
  // the address in the SAN only — the CN does not look like a host name, which OpenSSL would read
  // against the authority's DNS constraint and refuse — 825 days, what Apple's platforms accept, and
  // the authority after it in the file.
  private static readonly TimeSpan ServerLifetime = TimeSpan.FromDays(825);

  private static (string CertPem, string KeyPem) ServerCertificate(TlsAuthority tls, string address, DateTimeOffset now)
  {
    using var authority = X509Certificate2.CreateFromPem(tls.CertPem, tls.KeyPem);
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN=TreeAlarm VMS {address}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    var san = new SubjectAlternativeNameBuilder();
    san.AddIpAddress(IPAddress.Parse(address));
    request.CertificateExtensions.Add(san.Build());
    request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
    var serial = RandomNumberGenerator.GetBytes(16);
    serial[0] &= 0x7F;
    // Within the authority's own validity, which .NET insists on: one openssl made (a Linux core)
    // starts the moment it was made, not five minutes before; one in its last 825 days ends sooner.
    var notBefore = now.AddMinutes(-5) > authority.NotBefore.ToUniversalTime() ? now.AddMinutes(-5) : authority.NotBefore.ToUniversalTime();
    var notAfter = now + ServerLifetime < authority.NotAfter.ToUniversalTime() ? now + ServerLifetime : authority.NotAfter.ToUniversalTime();
    using var certificate = request.Create(authority, notBefore, notAfter, serial);
    return (certificate.ExportCertificatePem() + "\n" + tls.CertPem, key.ExportPkcs8PrivateKeyPem() + "\n");
  }

  /// The core's credentials and ports, and where a node reaches each of its services. The addresses
  /// are named one by one rather than left to CORE_ADDRESS, so that a core whose services sit behind
  /// different addresses (a cluster) needs no new package format; on a machine they are all the core's.
  private static string CoreEnv(IReadOnlyDictionary<string, string> env, string coreAddress)
  {
    var missing = RequiredCoreKeys.Where(k => string.IsNullOrEmpty(env.GetValueOrDefault(k))).ToList();
    if (missing.Count > 0)
      throw new InvalidOperationException($"this core's environment lacks {string.Join(", ", missing)}");

    var values = env.Where(kv => CoreKeys().IsMatch(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
    values["POSTGRES_HOST"] = Reachable(env.GetValueOrDefault("POSTGRES_HOST"), coreAddress);
    values["RABBITMQ_HOST"] = Reachable(env.GetValueOrDefault("RABBITMQ_HOST"), coreAddress);
    values["VMS_MTX_HOST"] = Reachable(env.GetValueOrDefault("VMS_MTX_HOST"), coreAddress);
    values["KEYCLOAK_URL"] = Uri.TryCreate(env.GetValueOrDefault("KEYCLOAK_URL"), UriKind.Absolute, out var keycloak)
      && Reachable(keycloak.Host, "") != ""
        ? keycloak.ToString().TrimEnd('/')
        : $"http://{coreAddress}:{env["KEYCLOAK_PORT"]}";

    return string.Concat(values.OrderBy(kv => kv.Key, StringComparer.Ordinal)
      .Select(kv => $"{kv.Key}={Quote(kv.Key, kv.Value)}\n"));
  }

  // The core's own value when another machine can use it — an address other than the loopback — and
  // the core's address otherwise: on Windows its services listen on 127.0.0.1 as well, and a compose
  // service name resolves only inside the core's network.
  private static string Reachable(string? value, string coreAddress) =>
    IPAddress.TryParse(value, out var ip) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any) ? value! : coreAddress;

  // As TaVmsHost's EnvFile.Quote: a value as it has to appear after '=' to be read back unchanged
  // there, and by compose and sed on Linux. The error names the key, never the value.
  private static string Quote(string key, string value)
  {
    if (value.Contains("${") || value.Contains('\n'))
      throw new InvalidOperationException($"The value of {key} cannot be written to an env file");
    if (!value.Any(c => char.IsWhiteSpace(c) || c is '#' or '"' or '\''))
      return value;
    if (!value.Contains('"'))
      return $"\"{value}\"";
    if (!value.Contains('\''))
      return $"'{value}'";
    throw new InvalidOperationException($"The value of {key} cannot be written to an env file: it holds both kinds of quote");
  }

  /// A value of a KEY=VALUE file, unquoted; null when the key is not there.
  public static string? ReadValue(string envText, string key)
  {
    foreach (var line in envText.Split('\n'))
    {
      var eq = line.IndexOf('=');
      if (eq <= 0 || line[..eq].Trim() != key) continue;
      var value = line[(eq + 1)..].Trim();
      return value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;
    }
    return null;
  }

  /// A JWT for Sentry's jwks validator: ES256, the subject an app's SPIFFE id, Sentry's audience, and
  /// no "exp" — a token is a machine's credential, revoked by a new key in Sentry's JWKS.
  public static string Token(ECDsa key, string subject, DateTimeOffset now)
  {
    var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", typ = "JWT", kid = KeyId }));
    var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
      new { sub = subject, aud = new[] { SentryId }, iat = now.ToUnixTimeSeconds() }));
    var signed = $"{header}.{payload}";
    // JWS wants r||s, not the DER sequence .NET produces by default.
    var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256,
      DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    return $"{signed}.{Base64Url(signature)}";
  }

  private static string Base64Url(byte[] bytes) =>
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
