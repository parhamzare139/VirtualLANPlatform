using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace VirtualLANPlatform.Core.Networking;

/// <summary>Outcome of a port-mapping attempt. <see cref="ExternalIp"/> is the address guests must dial.</summary>
public sealed record PortMapResult(bool Ok, string? ExternalIp, string? Error);

/// <summary>What a pre-flight probe found, which decides what the user needs to be told.</summary>
public enum UpnpState
{
    /// <summary>A gateway answered and reported a routable public address — hosting will work.</summary>
    Available,

    /// <summary>
    /// The gateway works, but the address it calls "external" is itself shared or private:
    /// the ISP is doing carrier-grade NAT. A mapping would be accepted and still lead
    /// nowhere, so this is worth catching before the user waits for a friend who can
    /// never arrive.
    /// </summary>
    CarrierNat,

    /// <summary>A device answered but exposes no WAN connection service to map through.</summary>
    NoService,

    /// <summary>Nothing answered — UPnP is switched off on the router, or blocked.</summary>
    NotFound
}

/// <summary>
/// A read-only look at the gateway. Carries the concrete addresses so instructions can
/// name the user's own router rather than a generic example.
/// </summary>
public sealed record UpnpProbe(
    UpnpState State,
    string?   RouterName = null,
    string?   GatewayIp  = null,
    string?   LocalIp    = null,
    string?   ExternalIp = null);

/// <summary>
/// Asks the home router to forward a UDP port to this machine, over UPnP IGD.
///
/// <para>
/// Without this, hosting only works for people on the same physical LAN: a guest's first
/// packet arrives at the host's router with no matching NAT entry and is dropped, so the
/// host never learns anyone called and never replies. Knowing the host's IP is not enough
/// — the router has to know which machine inside the house that packet belongs to.
/// </para>
/// <para>
/// Best-effort by design. Plenty of networks have UPnP switched off, and behind carrier
/// grade NAT no port can be opened at all; callers should treat failure as "LAN only"
/// rather than as an error worth blocking on.
/// </para>
/// </summary>
public sealed class PortMapper : IDisposable
{
    private const string SsdpAddress   = "239.255.255.250";
    private const ushort SsdpPort      = 1900;
    private const string IgdSearchType = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    private static readonly XNamespace DeviceNs = "urn:schemas-upnp-org:device-1-0";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };

    // Set once a mapping is in place, so it can be withdrawn on shutdown.
    private Uri?       _controlUrl;
    private string?    _serviceType;
    private ushort     _mappedPort;
    private IPAddress? _localIp;
    private string?    _routerName;

    /// <summary>The router's public address, once discovered.</summary>
    public string? ExternalIp { get; private set; }

    /// <summary>True while a mapping this object created is live on the router.</summary>
    public bool IsMapped => _mappedPort != 0;

    /// <summary>
    /// Looks at the gateway without changing anything: is UPnP answering, is there a WAN
    /// service, and is the address behind it actually reachable from the internet?
    /// Run this before hosting so a user whose router will not cooperate is told what to
    /// do, instead of hosting something nobody can reach.
    /// Never throws — every failure is a state.
    /// </summary>
    public async Task<UpnpProbe> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var gateway = await DiscoverAsync(ct).ConfigureAwait(false);
            if (gateway == null) return new UpnpProbe(UpnpState.NotFound);

            var (location, localIp) = gateway.Value;
            _localIp = localIp;

            string? gatewayIp = Uri.TryCreate(location, UriKind.Absolute, out var u) ? u.Host : null;

            if (!await LoadServiceAsync(location, ct).ConfigureAwait(false))
                return new UpnpProbe(UpnpState.NoService, _routerName, gatewayIp, localIp.ToString());

            ExternalIp = await GetExternalIpAsync(ct).ConfigureAwait(false);

            var state = IsReachableFromInternet(ExternalIp) ? UpnpState.Available : UpnpState.CarrierNat;
            return new UpnpProbe(state, _routerName, gatewayIp, localIp.ToString(), ExternalIp);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new UpnpProbe(UpnpState.NotFound); }
    }

    /// <summary>
    /// False when the router's "external" address is one nobody on the internet can route
    /// to — RFC1918 private space, or the RFC6598 100.64.0.0/10 block an ISP hands out
    /// when it is doing carrier-grade NAT.
    /// </summary>
    public static bool IsReachableFromInternet(string? ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) ||
            addr.AddressFamily != AddressFamily.InterNetwork) return false;

        byte[] b = addr.GetAddressBytes();
        if (b[0] == 10) return false;                              // 10.0.0.0/8
        if (b[0] == 127) return false;                             // loopback
        if (b[0] == 0) return false;                               // unspecified
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return false;              // 192.168.0.0/16
        if (b[0] == 169 && b[1] == 254) return false;              // link-local
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // 100.64.0.0/10 — CGNAT
        return true;
    }

    /// <summary>
    /// Discovers the gateway and forwards <paramref name="port"/> (UDP) to this machine.
    /// Never throws — a network without UPnP comes back as <c>Ok = false</c>.
    /// </summary>
    public async Task<PortMapResult> MapAsync(ushort port, string description, CancellationToken ct = default)
    {
        try
        {
            var probe = await ProbeAsync(ct).ConfigureAwait(false);
            if (probe.State is UpnpState.NotFound)
                return new PortMapResult(false, null, UI.Localization.Loc.T("Pm_NoUpnp"));
            if (probe.State is UpnpState.NoService)
                return new PortMapResult(false, null, UI.Localization.Loc.T("Pm_NoService"));

            IPAddress localIp = _localIp ?? IPAddress.Loopback;

            if (!await AddMappingAsync(port, localIp, description, ct).ConfigureAwait(false))
            {
                // A stale mapping from a previous run (or another app on the same port)
                // makes the router refuse. Clear it and try once more.
                await DeleteMappingAsync(port, ct).ConfigureAwait(false);
                if (!await AddMappingAsync(port, localIp, description, ct).ConfigureAwait(false))
                    return new PortMapResult(false, ExternalIp, UI.Localization.Loc.T("Pm_Refused"));
            }

            _mappedPort = port;
            return new PortMapResult(true, ExternalIp, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PortMapResult(false, ExternalIp, ex.Message);
        }
    }

    /// <summary>Withdraws the mapping. Safe to call when none was ever made.</summary>
    public async Task UnmapAsync()
    {
        if (_mappedPort == 0 || _controlUrl == null) return;
        ushort port = _mappedPort;
        _mappedPort = 0;
        try { await DeleteMappingAsync(port, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    // ── SSDP discovery ───────────────────────────────────────────────────────

    /// <summary>
    /// Multicasts an M-SEARCH from every local IPv4 interface and returns the first
    /// gateway that answers, along with the local address it answered on — that address
    /// is what the mapping must point at, and on a multi-homed machine (this app creates
    /// its own adapters) picking the wrong one silently forwards traffic into a void.
    /// </summary>
    private static async Task<(string Location, IPAddress LocalIp)?> DiscoverAsync(CancellationToken ct)
    {
        string request =
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {SsdpAddress}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            $"ST: {IgdSearchType}\r\n" +
            "\r\n";
        byte[] payload = Encoding.ASCII.GetBytes(request);
        var    target  = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);

        foreach (var local in LocalIPv4())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0));
                // Routers drop the odd multicast datagram; two costs nothing.
                await udp.SendAsync(payload, payload.Length, target).ConfigureAwait(false);
                await udp.SendAsync(payload, payload.Length, target).ConfigureAwait(false);

                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    var receive = udp.ReceiveAsync(ct).AsTask();
                    var slice   = deadline - DateTime.UtcNow;
                    if (slice <= TimeSpan.Zero) break;
                    if (await Task.WhenAny(receive, Task.Delay(slice, ct)).ConfigureAwait(false) != receive) break;

                    string text = Encoding.ASCII.GetString(receive.Result.Buffer);
                    foreach (string line in text.Split("\r\n"))
                    {
                        if (!line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)) continue;
                        string loc = line[9..].Trim();
                        if (loc.Length > 0) return (loc, local);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try the next interface */ }
        }

        return null;
    }

    private static IEnumerable<IPAddress> LocalIPv4() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                     && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct();

    // ── Device description ───────────────────────────────────────────────────

    /// <summary>Finds the WAN connection service and its control endpoint in the gateway's description.</summary>
    private async Task<bool> LoadServiceAsync(string location, CancellationToken ct)
    {
        string xml = await _http.GetStringAsync(location, ct).ConfigureAwait(false);
        var    doc = XDocument.Parse(xml);

        // Shown back to the user so the instructions name the box on their desk.
        _routerName = doc.Descendants(DeviceNs + "friendlyName").FirstOrDefault()?.Value;

        foreach (var svc in doc.Descendants(DeviceNs + "service"))
        {
            string type = svc.Element(DeviceNs + "serviceType")?.Value ?? "";
            // WANIPConnection on an Ethernet/cable WAN, WANPPPConnection on PPPoE/ADSL.
            if (!type.Contains("WANIPConnection", StringComparison.Ordinal) &&
                !type.Contains("WANPPPConnection", StringComparison.Ordinal))
                continue;

            string control = svc.Element(DeviceNs + "controlURL")?.Value ?? "";
            if (control.Length == 0) continue;

            _serviceType = type;
            _controlUrl  = new Uri(new Uri(location), control);
            return true;
        }

        return false;
    }

    // ── SOAP actions ─────────────────────────────────────────────────────────

    private async Task<string?> GetExternalIpAsync(CancellationToken ct)
    {
        string? body = await SoapAsync("GetExternalIPAddress", "", ct).ConfigureAwait(false);
        return body == null ? null : ReadElement(body, "NewExternalIPAddress");
    }

    private async Task<bool> AddMappingAsync(ushort port, IPAddress localIp, string description, CancellationToken ct)
    {
        // LeaseDuration 0 asks for a permanent mapping. Some routers reject that and
        // insist on a finite lease, so fall back to an hour; the app re-maps on every
        // start anyway, so a lease expiring between sessions costs nothing.
        foreach (int lease in new[] { 0, 3600 })
        {
            string args =
                "<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{port}</NewExternalPort>" +
                "<NewProtocol>UDP</NewProtocol>" +
                $"<NewInternalPort>{port}</NewInternalPort>" +
                $"<NewInternalClient>{localIp}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                $"<NewPortMappingDescription>{Escape(description)}</NewPortMappingDescription>" +
                $"<NewLeaseDuration>{lease}</NewLeaseDuration>";

            if (await SoapAsync("AddPortMapping", args, ct).ConfigureAwait(false) != null) return true;
        }
        return false;
    }

    private async Task DeleteMappingAsync(ushort port, CancellationToken ct)
    {
        string args =
            "<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            "<NewProtocol>UDP</NewProtocol>";
        await SoapAsync("DeletePortMapping", args, ct).ConfigureAwait(false);
    }

    /// <summary>Posts one SOAP action; returns the response body, or null if the router refused.</summary>
    private async Task<string?> SoapAsync(string action, string argsXml, CancellationToken ct)
    {
        if (_controlUrl == null || _serviceType == null) return null;

        string envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{_serviceType}\">{argsXml}</u:{action}>" +
            "</s:Body></s:Envelope>";

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "text/xml")
            };
            msg.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#{action}\"");

            using var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? body : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string? ReadElement(string xml, string name)
    {
        int open = xml.IndexOf($"<{name}>", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        int start = open + name.Length + 2;
        int close = xml.IndexOf($"</{name}>", start, StringComparison.OrdinalIgnoreCase);
        return close < 0 ? null : xml[start..close].Trim();
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public void Dispose()
    {
        // Every await inside uses ConfigureAwait(false), so blocking here cannot deadlock
        // against the UI thread that is calling Dispose during window close.
        try { UnmapAsync().GetAwaiter().GetResult(); } catch { }
        _http.Dispose();
    }
}
