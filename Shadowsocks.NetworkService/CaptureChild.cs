using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService;

internal sealed class CaptureChild : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private bool _disposed;

    private CaptureChild(
        Process process,
        NamedPipeServerStream pipe,
        StreamReader reader,
        StreamWriter writer,
        int tcpRedirectPort,
        int udpRedirectPort,
        bool dnsInterceptionActive,
        bool dnsFailClosedActive,
        bool managedRoutingActive,
        int managedRoutingRuleCount)
    {
        _process = process;
        _pipe = pipe;
        _reader = reader;
        _writer = writer;
        TcpRedirectPort = tcpRedirectPort;
        UdpRedirectPort = udpRedirectPort;
        DnsInterceptionActive = dnsInterceptionActive;
        DnsFailClosedActive = dnsFailClosedActive;
        ManagedRoutingActive = managedRoutingActive;
        ManagedRoutingRuleCount = managedRoutingRuleCount;
        StartedUtc = DateTime.UtcNow;
    }

    public int TcpRedirectPort { get; }
    public int UdpRedirectPort { get; }
    public DateTime StartedUtc { get; }
    public bool DnsInterceptionActive { get; }
    public bool DnsFailClosedActive { get; }
    public bool ManagedRoutingActive { get; }
    public int ManagedRoutingRuleCount { get; }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !_disposed && !_process.HasExited && _pipe.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    public static async Task<CaptureChild> StartAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string pipeName = "Shadowsocks.Capture." + Guid.NewGuid().ToString("N");
        NamedPipeServerStream pipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);
        Process? process = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;

        try
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to locate network service executable.");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            startInfo.ArgumentList.Add("--capture-pipe");
            startInfo.ArgumentList.Add(pipeName);
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start WinDivert capture child.");

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
            };

            request.Command = "start";
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(request, NetworkServiceJsonContext.Default.StartRequest)).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            ServiceResponse? response = string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.ServiceResponse);
            if (response?.Success != true)
            {
                throw new InvalidOperationException(response?.Message ?? "WinDivert capture child failed to start.");
            }

            return new CaptureChild(
                process,
                pipe,
                reader,
                writer,
                response.TcpRedirectPort,
                response.UdpRedirectPort,
                response.DnsInterceptionActive,
                response.DnsFailClosedActive,
                response.ManagedRoutingActive,
                response.ManagedRoutingRuleCount);
        }
        catch
        {
            writer?.Dispose();
            reader?.Dispose();
            pipe.Dispose();
            if (process is not null)
            {
                TryTerminate(process);
                process.Dispose();
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (_pipe.IsConnected && !_process.HasExited)
            {
                await _writer.WriteLineAsync("{\"command\":\"stop\"}").ConfigureAwait(false);
                Task<string?> readTask = _reader.ReadLineAsync(CancellationToken.None).AsTask();
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        try
        {
            if (!_process.HasExited && !_process.WaitForExit(3000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
        }

        _writer.Dispose();
        _reader.Dispose();
        _pipe.Dispose();
        _process.Dispose();
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
