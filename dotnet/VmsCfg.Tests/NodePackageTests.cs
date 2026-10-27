using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace VmsCfg.Tests;

// The package is read by TaVmsHost and scripts/distributed.sh in ../ta_install: what is in it, how it
// is laid out and what its tokens prove is a contract with both.
public sealed class NodePackageTests
{
  private const string CoreAddress = "192.168.1.100";
  private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

  private static readonly string SharedEnv =
    $"CORE_ADDRESS={CoreAddress}\nCONSUL_ENCRYPT=abc=\nDAPR_TRUST_ANCHORS=\"-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\"\n";

  // As a Windows core's VmsCfg sees its environment: its own services on the loopback.
  private static Dictionary<string, string> CoreEnvironment() => new()
  {
    ["POSTGRES_HOST"] = "127.0.0.1", ["POSTGRES_PORT"] = "5434", ["POSTGRES_USER"] = "postgres",
    ["POSTGRES_PASSWORD"] = "p#ss word", ["POSTGRES_STATE_DB"] = "ta_state", ["POSTGRES_FS_DB"] = "ta_fs",
    ["POSTGRES_CFG_DB"] = "ta_cfg",
    ["RABBITMQ_HOST"] = "127.0.0.1", ["RABBITMQ_PORT"] = "5673", ["RABBITMQ_DEFAULT_USER"] = "vms",
    ["RABBITMQ_DEFAULT_PASS"] = "secret",
    ["KEYCLOAK_URL"] = $"http://{CoreAddress}:5160", ["KEYCLOAK_PORT"] = "5160", ["KEYCLOAK_ADMIN_USER"] = "admin",
    ["KEYCLOAK_ADMIN_PASSWORD"] = "kc", ["KEYCLOAK_REALM_NAME"] = "tavms", ["KEYCLOAK_CLIENT_ID"] = "pubclient",
    ["VMS_MTX_HOST"] = CoreAddress, ["VMS_MEDIAMTX_READ_USER"] = "vms_read", ["VMS_MEDIAMTX_READ_PASS"] = "r",
    ["VMS_WHEP_SESSION_KEY"] = "whep",
    // Not the node's business: this machine's own paths and ports.
    ["VMS_STORAGE_BASE_PATH"] = @"C:\ProgramData\TreeAlarm\VMS\media_storage", ["WEB_VMS_PORT"] = "5134",
  };

  private const string NodeAddress = "192.168.1.82";

  // The core's TLS authority as openssl makes it on a Linux core: starting the moment it was made.
  private static readonly NodePackage.TlsAuthority Authority = MakeAuthority(Now);

  private static NodePackage.TlsAuthority MakeAuthority(DateTimeOffset notBefore, bool keyId = true)
  {
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN=TreeAlarm VMS CA {CoreAddress}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
    if (keyId)
      request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    using var ca = request.CreateSelfSigned(notBefore, notBefore.AddDays(3650));
    return new NodePackage.TlsAuthority(ca.ExportCertificatePem() + "\n", key.ExportPkcs8PrivateKeyPem() + "\n");
  }

  private static (Dictionary<string, (string Text, UnixFileMode Mode)> Files, ECDsa Key) Build(
    string name = "nhu", Dictionary<string, string>? env = null, NodePackage.TlsAuthority? authority = null)
  {
    var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var bytes = NodePackage.Build(name, NodeAddress, key.ExportECPrivateKeyPem(), authority ?? Authority, SharedEnv,
      env ?? CoreEnvironment(), Now);
    var files = new Dictionary<string, (string, UnixFileMode)>();
    using var tar = new TarReader(new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress));
    while (tar.GetNextEntry() is { } entry)
    {
      Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
      files.Add(entry.Name, (new StreamReader(entry.DataStream!).ReadToEnd(), entry.Mode));
    }
    return (files, key);
  }

  [Fact]
  public void It_holds_the_shared_values_the_cores_values_the_name_and_a_token_per_app_of_the_node()
  {
    var (files, _) = Build();

    Assert.Equal(
      new[]
      {
        "core.env", "name", "shared.env", "tls/ca.crt", "tls/server.crt", "tls/server.key",
        "tokens/vms-nhu.token", "tokens/vmsfs-nhu.token", "tokens/vmslogger-nhu.token",
        "tokens/vmsonvif-actors-nhu.token", "tokens/vmsonvif-nhu.token", "tokens/web-vms-nhu.token",
      },
      files.Keys.Order(StringComparer.Ordinal));
    Assert.Equal(SharedEnv, files["shared.env"].Text);
    Assert.Equal("nhu\n", files["name"].Text);
    Assert.All(files.Values, f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, f.Mode));
    Assert.All(files.Values, f => Assert.DoesNotContain('\r', f.Text));
  }

  // What the node's web presents on the LAN: a certificate for the node's own address, issued by the
  // core's authority, which follows it in the file and comes on its own beside it.
  [Fact]
  public void The_node_gets_a_certificate_for_its_address_from_the_cores_authority()
  {
    var (files, _) = Build();

    Assert.Equal(Authority.CertPem, files["tls/ca.crt"].Text);
    Assert.StartsWith("-----BEGIN PRIVATE KEY-----", files["tls/server.key"].Text);
    using var leaf = X509Certificate2.CreateFromPem(files["tls/server.crt"].Text, files["tls/server.key"].Text);
    Assert.Equal([System.Net.IPAddress.Parse(NodeAddress)],
      leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateIPAddresses());
    Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(),
      o => o.Value == "1.3.6.1.5.5.7.3.1");
    Assert.EndsWith(Authority.CertPem, files["tls/server.crt"].Text);

    using var root = X509Certificate2.CreateFromPem(Authority.CertPem);
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.VerificationTime = Now.UtcDateTime.AddMinutes(1);
    Assert.True(chain.Build(leaf));
    // Not before the authority, which here was made the moment the node joined.
    Assert.True(leaf.NotBefore >= root.NotBefore);
    Assert.InRange((leaf.NotAfter - leaf.NotBefore).TotalDays, 824, 826);
  }

  // OpenSSL 1.1 gives a CA a key identifier only when its configuration says so.
  [Fact]
  public void An_authority_without_a_key_identifier_still_issues_the_nodes_certificate()
  {
    var authority = MakeAuthority(Now, keyId: false);
    var (files, _) = Build(authority: authority);

    using var leaf = X509Certificate2.CreateFromPem(files["tls/server.crt"].Text, files["tls/server.key"].Text);
    using var root = X509Certificate2.CreateFromPem(authority.CertPem);
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.VerificationTime = Now.UtcDateTime.AddMinutes(1);
    Assert.True(chain.Build(leaf));
  }

  [Fact]
  public void The_cores_services_are_named_at_an_address_another_machine_reaches()
  {
    var coreEnv = Build().Files["core.env"].Text;

    // The loopback is the core's own view of its PostgreSQL and RabbitMQ.
    Assert.Contains($"POSTGRES_HOST={CoreAddress}\n", coreEnv);
    Assert.Contains($"RABBITMQ_HOST={CoreAddress}\n", coreEnv);
    Assert.Contains($"VMS_MTX_HOST={CoreAddress}\n", coreEnv);
    Assert.Contains($"KEYCLOAK_URL=http://{CoreAddress}:5160\n", coreEnv);
  }

  // A name with a domain in it resolves elsewhere too; one of a single label is a compose service's,
  // which resolves only inside the core's network.
  [Theory]
  [InlineData("https://auth.example.com", "https://auth.example.com")]
  [InlineData("http://keycloak:8080", $"http://{CoreAddress}:5160")]
  [InlineData("http://localhost:5160", $"http://{CoreAddress}:5160")]
  public void A_keycloak_named_by_a_domain_keeps_its_url(string url, string expected)
  {
    var env = CoreEnvironment();
    env["KEYCLOAK_URL"] = url;
    env["POSTGRES_HOST"] = "postgres";

    var coreEnv = Build(env: env).Files["core.env"].Text;
    Assert.Contains($"KEYCLOAK_URL={expected}\n", coreEnv);
    Assert.Contains($"POSTGRES_HOST={CoreAddress}\n", coreEnv);
  }

  [Fact]
  public void It_carries_the_cores_credentials_quoted_and_nothing_of_the_machines_own()
  {
    var coreEnv = Build().Files["core.env"].Text;

    Assert.Contains("POSTGRES_PASSWORD=\"p#ss word\"\n", coreEnv);
    Assert.Contains("POSTGRES_CFG_DB=ta_cfg\n", coreEnv);
    Assert.Contains("VMS_WHEP_SESSION_KEY=whep\n", coreEnv);
    Assert.DoesNotContain("VMS_STORAGE_BASE_PATH", coreEnv);
    Assert.DoesNotContain("WEB_VMS_PORT", coreEnv);
  }

  [Fact]
  public void A_core_without_a_value_every_node_needs_makes_no_package()
  {
    var env = CoreEnvironment();
    env.Remove("VMS_WHEP_SESSION_KEY");

    var error = Assert.Throws<InvalidOperationException>(() => Build(env: env));
    Assert.Contains("VMS_WHEP_SESSION_KEY", error.Message);
  }

  [Fact]
  public void Its_tokens_are_es256_jwts_for_the_apps_spiffe_id_that_verify_against_the_cores_key()
  {
    var (files, key) = Build();
    var token = files["tokens/vmsonvif-actors-nhu.token"].Text;

    var parts = token.Split('.');
    Assert.Equal(3, parts.Length);
    Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]),
      HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
    Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
    Assert.Equal("tavms", header.RootElement.GetProperty("kid").GetString());
    using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
    Assert.Equal("spiffe://public/ns/default/vmsonvif-actors-nhu", payload.RootElement.GetProperty("sub").GetString());
    Assert.Equal("spiffe://localhost/ns/default/dapr-sentry", payload.RootElement.GetProperty("aud")[0].GetString());
    Assert.False(payload.RootElement.TryGetProperty("exp", out _));
  }

  [Theory]
  [InlineData("nhu")]
  [InlineData("hall-2")]
  [InlineData("7")]
  public void A_name_of_lower_case_letters_digits_and_dashes_names_a_node(string name) =>
    Assert.Null(NodePackage.NameProblem(name));

  [Theory]
  [InlineData("core")]
  [InlineData("Hall")]
  [InlineData("-hall")]
  [InlineData("hall-")]
  [InlineData("hall_2")]
  [InlineData("")]
  public void Anything_else_does_not(string name) => Assert.NotNull(NodePackage.NameProblem(name));

  [Theory]
  [InlineData("192.168.1.82", true)]
  [InlineData("10.0.0.5", true)]
  [InlineData("127.0.0.1", false)]
  [InlineData("::1", false)]
  [InlineData("fe80::1", false)]
  [InlineData("node.local", false)]
  public void A_node_is_reached_at_an_ipv4_address_other_than_the_loopback(string address, bool ok) =>
    Assert.Equal(ok, NodePackage.AddressProblem(address) is null);

  [Fact]
  public void A_nodes_pair_and_app_ids_carry_its_name()
  {
    Assert.Equal(("vms-nhu", "vmsfs-nhu", "vmsonvif-nhu", "vmslogger-nhu"), NodePackage.Pair("nhu"));
    Assert.Contains("web-vms-nhu", NodePackage.AppIds("nhu"));
  }

  private static byte[] FromBase64Url(string s)
  {
    s = s.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
  }
}
