using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
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

  private static (Dictionary<string, (string Text, UnixFileMode Mode)> Files, ECDsa Key) Build(
    string name = "nhu", Dictionary<string, string>? env = null)
  {
    var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var bytes = NodePackage.Build(name, key.ExportECPrivateKeyPem(), SharedEnv, env ?? CoreEnvironment(), Now);
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
        "core.env", "name", "shared.env",
        "tokens/vms-nhu.token", "tokens/vmsfs-nhu.token", "tokens/vmslogger-nhu.token",
        "tokens/vmsonvif-actors-nhu.token", "tokens/vmsonvif-nhu.token", "tokens/web-vms-nhu.token",
      },
      files.Keys.Order(StringComparer.Ordinal));
    Assert.Equal(SharedEnv, files["shared.env"].Text);
    Assert.Equal("nhu\n", files["name"].Text);
    Assert.All(files.Values, f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, f.Mode));
    Assert.All(files.Values, f => Assert.DoesNotContain('\r', f.Text));
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
