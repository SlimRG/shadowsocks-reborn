using System.Text.Json;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService;

internal static class NetworkServiceResponses
{
    private static string ServiceVersion => typeof(Program).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";

    public static ServiceResponse Success(string message) => new()
    {
        Success = true,
        Message = message,
        Version = ServiceVersion,
    };

    public static ServiceResponse Failure(string message) => new()
    {
        Success = false,
        Message = message,
        Version = ServiceVersion,
    };

    public static ServiceResponse CaptureStatus(CaptureChild child, string message)
    {
        bool active = child.IsAlive;
        return new ServiceResponse
        {
            Success = true,
            Message = message,
            Version = ServiceVersion,
            CaptureActive = active,
            TcpRedirectPort = active ? child.TcpRedirectPort : 0,
            UdpRedirectPort = active ? child.UdpRedirectPort : 0,
            DnsInterceptionActive = active && child.DnsInterceptionActive,
            DnsFailClosedActive = active && child.DnsFailClosedActive,
        };
    }

    public static Task SendAsync(StreamWriter writer, ServiceResponse response)
        => writer.WriteLineAsync(JsonSerializer.Serialize(response, NetworkServiceJsonContext.Default.ServiceResponse));
}
