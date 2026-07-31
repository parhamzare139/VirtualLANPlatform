using System.Net;
using System.Net.Http;

namespace VirtualLANPlatform.Core.Networking;

/// <summary>
/// Discovers the machine's public IPv4 address using well-known HTTP services.
/// No Room data, voice, chat or file traffic passes through these services —
/// they are used exclusively for IP discovery.
/// </summary>
public sealed class PublicIPDiscovery : IDisposable
{
    private static readonly string[] Services =
    [
        "https://api.ipify.org",
        "https://api4.my-ip.io/ip",
        "https://checkip.amazonaws.com",
        "https://icanhazip.com"
    ];

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private IPAddress? _cachedIp;
    private DateTime _cacheExpiry = DateTime.MinValue;

    public PublicIPDiscovery()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VirtualLANPlatform/1.0");
    }

    /// <summary>
    /// Returns the public IP, using cache if still valid.
    /// Returns null when all discovery services are unreachable.
    /// </summary>
    public async Task<IPAddress?> GetPublicIPAsync(CancellationToken ct = default)
    {
        if (_cachedIp != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedIp;

        foreach (string service in Services)
        {
            try
            {
                string text = (await _http.GetStringAsync(service, ct)).Trim();
                if (IPAddress.TryParse(text, out IPAddress? ip))
                {
                    _cachedIp = ip;
                    _cacheExpiry = DateTime.UtcNow + CacheDuration;
                    return ip;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try next service */ }
        }

        return null;
    }

    public void InvalidateCache() => _cacheExpiry = DateTime.MinValue;

    public void Dispose() => _http.Dispose();
}
