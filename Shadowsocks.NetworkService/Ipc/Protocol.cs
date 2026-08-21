using System.Text.Json.Serialization;

namespace Shadowsocks.NetworkService.Ipc;

internal enum RouteAction
{
    Default = 0,
    Proxy = 1,
    Direct = 2,
    Block = 3,
}

internal enum DnsPolicyMode
{
    System = 0,
    Direct = 1,
    Proxy = 2,
    CustomDoh = 3,
    DnsCrypt = 4,
}

internal sealed class ApplicationRuleDto
{
    public bool Enabled { get; set; } = true;
    public string Application { get; set; } = string.Empty;
    public RouteAction Action { get; set; } = RouteAction.Proxy;
}

internal sealed class DnsPolicyDto
{
    public DnsPolicyMode Mode { get; set; } = DnsPolicyMode.System;
    public string DirectDnsServer { get; set; } = string.Empty;
    public string DirectDnsFallbackServer { get; set; } = string.Empty;
    public bool DirectDnsRouteThroughShadowsocks { get; set; }
    public string CustomDohUrl { get; set; } = string.Empty;
    public bool CustomDohRouteThroughShadowsocks { get; set; }
    public int DnsCryptPort { get; set; }
    public int DnsCryptProcessId { get; set; }
    public string DnsCryptComponentRoot { get; set; } = string.Empty;
    public bool FailClosed { get; set; } = true;

    [JsonIgnore]
    public bool DirectDnsReady => Mode == DnsPolicyMode.Direct
        && System.Net.IPAddress.TryParse(DirectDnsServer?.Trim(), out _)
        && (string.IsNullOrWhiteSpace(DirectDnsFallbackServer)
            || System.Net.IPAddress.TryParse(DirectDnsFallbackServer.Trim(), out _));

    [JsonIgnore]
    public bool DnsCryptReady => Mode == DnsPolicyMode.DnsCrypt
        && DnsCryptPort is >= 1 and <= 65535
        && DnsCryptProcessId > 0;

    [JsonIgnore]
    public bool CustomDohReady => Mode == DnsPolicyMode.CustomDoh
        && Uri.TryCreate(CustomDohUrl, UriKind.Absolute, out Uri? uri)
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}

internal sealed class StartRequest
{
    public string Command { get; set; } = "start";
    public string WinDivertDirectory { get; set; } = string.Empty;
    public string LocalProxyHost { get; set; } = "127.0.0.1";
    public int LocalProxyPort { get; set; } = 1080;
    public int MainProcessId { get; set; }
    public RouteAction DefaultRoute { get; set; } = RouteAction.Direct;
    public List<ApplicationRuleDto> ApplicationRules { get; set; } = [];
    public List<int> ExcludedProcessIds { get; set; } = [];
    public DnsPolicyDto DnsPolicy { get; set; } = new();
}

internal sealed class ControlRequest
{
    public string Command { get; set; } = string.Empty;
}

internal sealed class ServiceResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int TcpRedirectPort { get; set; }
    public int UdpRedirectPort { get; set; }
    public bool CaptureActive { get; set; }
    public bool DnsInterceptionActive { get; set; }
    public bool DnsFailClosedActive { get; set; }
    public bool DriverRemoved { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StartRequest))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ServiceResponse))]
internal partial class NetworkServiceJsonContext : JsonSerializerContext
{
}
