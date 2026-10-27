using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace web_srv;

/// <summary>
/// HTTPS on the LAN, for a machine of a deployment over several (CLAUDE.md, "Distributed layout"):
/// the certificate of this machine's address, issued by the core's own certificate authority, which
/// follows it in the file. <c>WEB_VMS_TLS_CERT</c> and <c>WEB_VMS_TLS_KEY</c> are optional, and
/// together: a standalone installation sets neither and listens on plain HTTP on every address, as it
/// always has.
/// </summary>
/// <remarks>
/// With them the plain-HTTP port stays on the loopback, for this web's own sidecar and for the
/// shortcut on the machine itself (http://localhost), and the same port on <c>DAPR_HOST_IP</c>, the
/// machine's address on the LAN, speaks TLS only. Nothing changes in the firewall or in the ports a
/// node is told.
///
/// The handshake carries this machine's certificate alone: .NET leaves a self-signed root out of the
/// chain it sends. Whoever needs the authority — a browser to trust, a machine joining to check
/// against its fingerprint — downloads it (<c>GET /api/deployment/ca.crt</c>).
/// </remarks>
public sealed class TlsEndpoint
{
  private readonly X509Certificate2 _certificate;
  private readonly IPAddress _address;

  /// <summary>The core's certificate authority, the last certificate of the file.</summary>
  public X509Certificate2 Authority { get; }

  /// <summary>SHA-256 of <see cref="Authority"/>, in hex pairs, as browsers and openssl show it.</summary>
  public string AuthorityFingerprint => string.Join(':', SHA256.HashData(Authority.RawData).Select(b => b.ToString("X2")));

  private TlsEndpoint(X509Certificate2 certificate, X509Certificate2 authority, IPAddress address)
  {
    // SChannel will not use a key that lives only in memory, which is what a PEM import gives.
    _certificate = OperatingSystem.IsWindows()
      ? X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null)
      : certificate;
    _address = address;
    Authority = authority;
  }

  /// <summary>Null when neither variable is set; throws when only one is, or a file does not load.</summary>
  public static TlsEndpoint? FromEnvironment()
  {
    var certPath = Environment.GetEnvironmentVariable("WEB_VMS_TLS_CERT");
    var keyPath = Environment.GetEnvironmentVariable("WEB_VMS_TLS_KEY");
    if (string.IsNullOrEmpty(certPath) && string.IsNullOrEmpty(keyPath))
      return null;
    if (string.IsNullOrEmpty(certPath) || string.IsNullOrEmpty(keyPath))
      throw new InvalidOperationException("WEB_VMS_TLS_CERT and WEB_VMS_TLS_KEY are set together or not at all");
    var host = Environment.GetEnvironmentVariable("DAPR_HOST_IP");
    if (!IPAddress.TryParse(host, out var address) || IPAddress.IsLoopback(address))
      throw new InvalidOperationException(
        $"WEB_VMS_TLS_CERT is set, so DAPR_HOST_IP must be this machine's address on the network, not '{host}'");
    return Load(certPath, keyPath, address);
  }

  public static TlsEndpoint Load(string certPath, string keyPath, IPAddress address)
  {
    var all = new X509Certificate2Collection();
    all.ImportFromPemFile(certPath);
    if (all.Count < 2)
      throw new InvalidOperationException($"{certPath} must hold this machine's certificate followed by the core's authority");
    return new TlsEndpoint(X509Certificate2.CreateFromPemFile(certPath, keyPath), all[^1], address);
  }

  public void Listen(KestrelServerOptions options, int port)
  {
    options.Listen(IPAddress.Loopback, port);
    options.Listen(_address, port, listen => listen.UseHttps(_certificate));
  }
}
