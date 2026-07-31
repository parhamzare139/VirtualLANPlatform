using System.Net;
using VirtualLANPlatform.Core.Networking;

namespace VirtualLANPlatform.Tests.Core.Networking;

public class ConnectionCodeEngineTests
{
    // ── Encode / Decode roundtrip ─────────────────────────────────────────────

    [Fact]
    public void Roundtrip_IPv4_ReturnsOriginal()
    {
        var ip   = IPAddress.Parse("93.184.216.34");
        ushort port = 42777;

        string code = ConnectionCodeEngine.Encode(ip, port);
        var (gotIp, gotPort) = ConnectionCodeEngine.Decode(code);

        Assert.Equal(ip, gotIp);
        Assert.Equal(port, gotPort);
    }

    [Fact]
    public void Roundtrip_IPv6_ReturnsOriginal()
    {
        var ip   = IPAddress.Parse("2001:db8::1");
        ushort port = 12345;

        string code = ConnectionCodeEngine.Encode(ip, port);
        var (gotIp, gotPort) = ConnectionCodeEngine.Decode(code);

        Assert.Equal(ip, gotIp);
        Assert.Equal(port, gotPort);
    }

    [Fact]
    public void Roundtrip_LocalhostPort80()
    {
        var ip   = IPAddress.Parse("127.0.0.1");
        ushort port = 80;

        string code = ConnectionCodeEngine.Encode(ip, port);
        var (gotIp, gotPort) = ConnectionCodeEngine.Decode(code);

        Assert.Equal(ip, gotIp);
        Assert.Equal(port, gotPort);
    }

    // ── Code changes when IP or Port changes ──────────────────────────────────

    [Fact]
    public void DifferentIP_ProducesDifferentCode()
    {
        ushort port = 5000;
        string code1 = ConnectionCodeEngine.Encode(IPAddress.Parse("1.2.3.4"), port);
        string code2 = ConnectionCodeEngine.Encode(IPAddress.Parse("1.2.3.5"), port);

        Assert.NotEqual(code1, code2);
    }

    [Fact]
    public void DifferentPort_ProducesDifferentCode()
    {
        var ip = IPAddress.Parse("1.2.3.4");
        string code1 = ConnectionCodeEngine.Encode(ip, 5000);
        string code2 = ConnectionCodeEngine.Encode(ip, 5001);

        Assert.NotEqual(code1, code2);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void InvalidCode_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => ConnectionCodeEngine.Decode("INVALID_CODE"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    public void ShortCode_ThrowsFormatException(string code)
    {
        Assert.Throws<FormatException>(() => ConnectionCodeEngine.Decode(code));
    }

    [Fact]
    public void TamperedCode_ThrowsFormatException()
    {
        var ip = IPAddress.Parse("10.0.0.1");
        string code = ConnectionCodeEngine.Encode(ip, 8080);

        // Flip the last character
        char[] chars = code.ToCharArray();
        chars[^1] = chars[^1] == '1' ? '2' : '1';
        string tampered = new(chars);

        Assert.Throws<FormatException>(() => ConnectionCodeEngine.Decode(tampered));
    }

    // ── Code properties ───────────────────────────────────────────────────────

    [Fact]
    public void IPv4Code_HasExpectedLength()
    {
        string code = ConnectionCodeEngine.Encode(IPAddress.Parse("1.2.3.4"), 1234);
        // 8 raw bytes → Base58 ≈ 11 chars (can vary slightly)
        Assert.InRange(code.Length, 9, 13);
    }

    [Fact]
    public void Code_ContainsOnlyBase58Characters()
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        string code = ConnectionCodeEngine.Encode(IPAddress.Parse("8.8.8.8"), 53);

        foreach (char c in code)
            Assert.Contains(c, alphabet);
    }

    [Fact]
    public void Code_DoesNotContainAmbiguousCharacters()
    {
        string code = ConnectionCodeEngine.Encode(IPAddress.Parse("8.8.8.8"), 53);

        Assert.DoesNotContain('0', code);
        Assert.DoesNotContain('O', code);
        Assert.DoesNotContain('I', code);
        Assert.DoesNotContain('l', code);
    }
}
