using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Xml.Linq;

namespace OnvifLib
{
  public record DiscoveredDevice(
    string EndpointUuid,
    string Ip,
    int Port,
    IReadOnlyList<string> XAddrs,
    IReadOnlyList<string> Scopes,
    string? Hardware,
    string? Name);

  /// WS-Discovery (SOAP-over-UDP multicast) prober. Distinct from CameraScanner — that one
  /// brute-forces an IP range with HTTP, this one asks devices to announce themselves, so it
  /// finds the camera's actual ONVIF port even when it isn't the default.
  public static class WsDiscovery
  {
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private const int MulticastPort = 3702;

    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace Disc = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    private static readonly XNamespace Dn = "http://www.onvif.org/ver10/network/wsdl";

    public static async Task<List<DiscoveredDevice>> ProbeAsync(
      TimeSpan timeout, CancellationToken ct, IOnvifLogger? logger = null)
    {
      var found = new Dictionary<string, DiscoveredDevice>();
      var lockObj = new object();

      using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      cts.CancelAfter(timeout);

      var listenTasks = GetCandidateInterfaces()
        .Select(addr => ListenOnInterfaceAsync(addr, found, lockObj, cts.Token, logger))
        .ToList();

      try
      {
        await Task.WhenAll(listenTasks);
      }
      catch (OperationCanceledException)
      {
        // expected once the timeout elapses
      }

      return found.Values.ToList();
    }

    private static IEnumerable<IPAddress> GetCandidateInterfaces()
    {
      foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
      {
        if (nic.OperationalStatus != OperationalStatus.Up)
          continue;
        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
          continue;
        if (!nic.Supports(NetworkInterfaceComponent.IPv4))
          continue;

        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
        {
          if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
            yield return ua.Address;
        }
      }
    }

    private static async Task ListenOnInterfaceAsync(
      IPAddress localAddress,
      Dictionary<string, DiscoveredDevice> found,
      object lockObj,
      CancellationToken ct,
      IOnvifLogger? logger)
    {
      UdpClient client;
      try
      {
        client = new UdpClient(new IPEndPoint(localAddress, 0));
        client.JoinMulticastGroup(MulticastAddress, localAddress);
      }
      catch (Exception ex)
      {
        logger?.Warning($"WS-Discovery: cannot bind/join multicast on {localAddress}: {ex.Message}");
        return;
      }

      using (client)
      {
        try
        {
          var probe = BuildProbeMessage();
          await client.SendAsync(probe, probe.Length, new IPEndPoint(MulticastAddress, MulticastPort));

          while (!ct.IsCancellationRequested)
          {
            UdpReceiveResult result;
            try
            {
              var receiveTask = client.ReceiveAsync();
              result = await receiveTask.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
              break;
            }

            try
            {
              var device = ParseProbeMatch(result.Buffer);
              if (device != null)
              {
                lock (lockObj)
                {
                  found[device.EndpointUuid] = device;
                }
              }
            }
            catch (Exception ex)
            {
              logger?.Debug($"WS-Discovery: failed to parse response from {result.RemoteEndPoint}: {ex.Message}");
            }
          }
        }
        catch (Exception ex)
        {
          logger?.Warning($"WS-Discovery: probe on {localAddress} failed: {ex.Message}");
        }
      }
    }

    private static byte[] BuildProbeMessage()
    {
      var messageId = $"uuid:{Guid.NewGuid()}";

      var envelope = new XElement(Soap + "Envelope",
        new XAttribute(XNamespace.Xmlns + "soap", Soap.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "wsa", Wsa.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "d", Disc.NamespaceName),
        new XAttribute(XNamespace.Xmlns + "dn", Dn.NamespaceName),
        new XElement(Soap + "Header",
          new XElement(Wsa + "MessageID", messageId),
          new XElement(Wsa + "To", "urn:schemas-xmlsoap-org:ws:2005:04:discovery"),
          new XElement(Wsa + "Action", "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe")),
        new XElement(Soap + "Body",
          new XElement(Disc + "Probe",
            new XElement(Disc + "Types", "dn:NetworkVideoTransmitter"))));

      var doc = new XDocument(envelope);
      return System.Text.Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private static DiscoveredDevice? ParseProbeMatch(byte[] buffer)
    {
      var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer));

      var probeMatch = doc.Descendants(Disc + "ProbeMatch").FirstOrDefault();
      if (probeMatch == null)
        return null;

      var endpointUuid = probeMatch.Descendants(Wsa + "Address").FirstOrDefault()?.Value?.Trim();
      var xAddrsRaw = probeMatch.Element(Disc + "XAddrs")?.Value ?? string.Empty;
      var scopesRaw = probeMatch.Element(Disc + "Scopes")?.Value ?? string.Empty;

      var xAddrs = xAddrsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      var scopes = scopesRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

      if (xAddrs.Length == 0)
        return null;

      var firstXAddr = new Uri(xAddrs[0]);

      return new DiscoveredDevice(
        EndpointUuid: endpointUuid ?? xAddrs[0],
        Ip: firstXAddr.Host,
        Port: firstXAddr.Port,
        XAddrs: xAddrs,
        Scopes: scopes,
        Hardware: ExtractScopeValue(scopes, "onvif://www.onvif.org/hardware/"),
        Name: ExtractScopeValue(scopes, "onvif://www.onvif.org/name/"));
    }

    private static string? ExtractScopeValue(string[] scopes, string prefix)
    {
      var match = scopes.FirstOrDefault(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
      if (match == null)
        return null;

      var raw = match[prefix.Length..];
      return Uri.UnescapeDataString(raw);
    }
  }
}
