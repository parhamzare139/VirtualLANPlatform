using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace VirtualLANPlatform.Core.Networking;

/// <summary>
/// Minimal UPnP IGD (Internet Gateway Device) client.
/// Used to automatically create UDP port mappings on the home router
/// so that guests can reach the host without manual port forwarding.
/// </summary>
public sealed class UPnPManager : IDisposable
{
    private const string MulticastAddress = "239.255.255.250";
    private const int    SsdpPort         = 1900;
    private const string IgdSearchTarget  = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string? _controlUrl;
    private readonly List<ushort> _mappedPorts = [];

    // ── Discovery ──────────────────────────────────────────────────────────────

    public async Task<bool> TryDiscoverAsync(CancellationToken ct = default)
    {
        string request =
            $"M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {MulticastAddress}:{SsdpPort}\r\n" +
            $"MAN: \"ssdp:discover\"\r\n" +
            $"MX: 3\r\n" +
            $"ST: {IgdSearchTarget}\r\n\r\n";

        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        var endpoint = new IPEndPoint(IPAddress.Parse(MulticastAddress), SsdpPort);

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 4000;
            await udp.SendAsync(requestBytes, endpoint, ct);

            while (true)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(ct); }
                catch { break; }

                string response = Encoding.UTF8.GetString(result.Buffer);
                string? location = ExtractSsdpHeader(response, "LOCATION");
                if (location == null) continue;

                string? controlUrl = await FetchControlUrlAsync(location, ct);
                if (controlUrl != null)
                {
                    _controlUrl = controlUrl;
                    return true;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* no UPnP gateway found */ }

        return false;
    }

    // ── Port Mapping ───────────────────────────────────────────────────────────

    public async Task<bool> AddPortMappingAsync(
        ushort externalPort, ushort internalPort, string internalClient,
        string description, CancellationToken ct = default)
    {
        if (_controlUrl == null) return false;

        string soap = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:AddPortMapping xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{externalPort}</NewExternalPort>
                  <NewProtocol>UDP</NewProtocol>
                  <NewInternalPort>{internalPort}</NewInternalPort>
                  <NewInternalClient>{internalClient}</NewInternalClient>
                  <NewEnabled>1</NewEnabled>
                  <NewPortMappingDescription>{description}</NewPortMappingDescription>
                  <NewLeaseDuration>0</NewLeaseDuration>
                </u:AddPortMapping>
              </s:Body>
            </s:Envelope>
            """;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(soap, Encoding.UTF8, "text/xml")
            };
            req.Headers.Add("SOAPAction",
                "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");

            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                _mappedPorts.Add(externalPort);
                return true;
            }
        }
        catch { }

        return false;
    }

    public async Task RemovePortMappingAsync(ushort externalPort, CancellationToken ct = default)
    {
        if (_controlUrl == null) return;

        string soap = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:DeletePortMapping xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{externalPort}</NewExternalPort>
                  <NewProtocol>UDP</NewProtocol>
                </u:DeletePortMapping>
              </s:Body>
            </s:Envelope>
            """;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(soap, Encoding.UTF8, "text/xml")
            };
            req.Headers.Add("SOAPAction",
                "\"urn:schemas-upnp-org:service:WANIPConnection:1#DeletePortMapping\"");

            await _http.SendAsync(req, ct);
            _mappedPorts.Remove(externalPort);
        }
        catch { }
    }

    public async Task CleanupAllAsync()
    {
        foreach (ushort port in _mappedPorts.ToList())
            await RemovePortMappingAsync(port);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<string?> FetchControlUrlAsync(string locationUrl, CancellationToken ct)
    {
        try
        {
            string xml = await _http.GetStringAsync(locationUrl, ct);
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("d", "urn:schemas-upnp-org:device-1-0");

            string[] serviceTypes = ["WANIPConnection:1", "WANPPPConnection:1", "WANIPConnection:2"];
            foreach (string st in serviceTypes)
            {
                var node = doc.SelectSingleNode(
                    $"//d:service[d:serviceType[contains(.,'{st}')]]/d:controlURL", ns);

                if (node?.InnerText is string path && path.Length > 0)
                {
                    if (path.StartsWith('/'))
                    {
                        var uri = new Uri(locationUrl);
                        return $"{uri.Scheme}://{uri.Host}:{uri.Port}{path}";
                    }
                    return path;
                }
            }
        }
        catch { }

        return null;
    }

    private static string? ExtractSsdpHeader(string response, string name)
    {
        foreach (string line in response.Split('\n'))
        {
            if (line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                return line[(name.Length + 1)..].Trim();
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
