using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shadowsocks.NetworkService.Ipc;
using Shadowsocks.NetworkService.Routing;

namespace Shadowsocks.NetworkService;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? startupLogPath = GetArgument(args, "--startup-log");
        string? capturePipeName = GetArgument(args, "--capture-pipe");
        if (!string.IsNullOrWhiteSpace(capturePipeName))
        {
            return await RunCaptureChildAsync(capturePipeName).ConfigureAwait(false);
        }

        string? pipeName = GetArgument(args, "--pipe");
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            return 2;
        }

        return await RunBrokerAsync(pipeName, startupLogPath).ConfigureAwait(false);
    }

    private static async Task<int> RunBrokerAsync(string pipeName, string? startupLogPath)
    {
        CaptureChild? child = null;
        StartRequest? lastRequest = null;
        bool gameMode = false;

        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
            };

            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ControlRequest? control;
                try
                {
                    control = JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.ControlRequest);
                }
                catch (Exception exception)
                {
                    await SendAsync(writer, Failure("Invalid control request: " + exception.Message)).ConfigureAwait(false);
                    continue;
                }

                string command = control?.Command ?? string.Empty;
                if (command.Equals("start", StringComparison.OrdinalIgnoreCase)
                    || command.Equals("restart", StringComparison.OrdinalIgnoreCase))
                {
                    StartRequest? request = JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.StartRequest);
                    if (request is null)
                    {
                        await SendAsync(writer, Failure("Invalid capture configuration.")).ConfigureAwait(false);
                        continue;
                    }

                    lastRequest = request;
                    if (gameMode)
                    {
                        await SendAsync(writer, Success("Configuration stored; Game Mode remains active.")).ConfigureAwait(false);
                        continue;
                    }

                    if (child is not null)
                    {
                        await child.DisposeAsync().ConfigureAwait(false);
                        child = null;
                    }

                    try
                    {
                        child = await CaptureChild.StartAsync(request).ConfigureAwait(false);
                        await SendAsync(writer, new ServiceResponse
                        {
                            Success = true,
                            Message = "Admin capture active.",
                            TcpRedirectPort = child.TcpRedirectPort,
                            UdpRedirectPort = child.UdpRedirectPort,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        await SendAsync(writer, Failure(exception.Message)).ConfigureAwait(false);
                    }
                    continue;
                }

                if (command.Equals("game-on", StringComparison.OrdinalIgnoreCase))
                {
                    gameMode = true;
                    if (child is not null)
                    {
                        await child.DisposeAsync().ConfigureAwait(false);
                        child = null;
                    }

                    bool removed = DriverServiceCleaner.TryRemove();
                    await SendAsync(writer, new ServiceResponse
                    {
                        Success = true,
                        Message = removed
                            ? "Game Mode active; WinDivert capture and driver removed."
                            : "Game Mode active; capture stopped, driver cleanup reported a warning.",
                        DriverRemoved = removed,
                    }).ConfigureAwait(false);
                    continue;
                }

                if (command.Equals("game-off", StringComparison.OrdinalIgnoreCase))
                {
                    gameMode = false;
                    if (lastRequest is null)
                    {
                        await SendAsync(writer, Failure("No Admin Mode configuration is available.")).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        if (child is not null)
                        {
                            await child.DisposeAsync().ConfigureAwait(false);
                            child = null;
                        }

                        child = await CaptureChild.StartAsync(lastRequest).ConfigureAwait(false);
                        await SendAsync(writer, new ServiceResponse
                        {
                            Success = true,
                            Message = "Admin capture restored.",
                            TcpRedirectPort = child.TcpRedirectPort,
                            UdpRedirectPort = child.UdpRedirectPort,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        await SendAsync(writer, Failure(exception.Message)).ConfigureAwait(false);
                    }
                    continue;
                }

                if (command.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    await SendAsync(writer, Success("pong")).ConfigureAwait(false);
                    continue;
                }

                if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    if (child is not null)
                    {
                        await child.DisposeAsync().ConfigureAwait(false);
                        child = null;
                    }

                    bool removed = DriverServiceCleaner.TryRemove();
                    await SendAsync(writer, new ServiceResponse
                    {
                        Success = true,
                        Message = "Stopping.",
                        DriverRemoved = removed,
                    }).ConfigureAwait(false);
                    break;
                }

                await SendAsync(writer, Failure("Unknown command.")).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException exception)
        {
            TryWriteStartupFailure(startupLogPath, "NetworkService broker startup timed out or was cancelled: " + exception);
            return 4;
        }
        catch (Exception exception)
        {
            TryWriteStartupFailure(startupLogPath, "NetworkService broker failed before the control channel was established: " + exception);
            return 1;
        }
        finally
        {
            if (child is not null)
            {
                try
                {
                    await child.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
            DriverServiceCleaner.TryRemove();
        }
    }

    private static async Task<int> RunCaptureChildAsync(string pipeName)
    {
        try
        {
            using NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            using StreamReader reader = new(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using StreamWriter writer = new(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
            };

            string? startLine = await reader.ReadLineAsync().ConfigureAwait(false);
            StartRequest? request = string.IsNullOrWhiteSpace(startLine)
                ? null
                : JsonSerializer.Deserialize(startLine, NetworkServiceJsonContext.Default.StartRequest);
            if (request is null)
            {
                await SendAsync(writer, Failure("Invalid capture child request.")).ConfigureAwait(false);
                return 3;
            }

            await using WinDivertTransparentRouter router = new(request);
            try
            {
                router.Start();
            }
            catch (Exception exception)
            {
                await SendAsync(writer, Failure(exception.Message)).ConfigureAwait(false);
                return 5;
            }

            await SendAsync(writer, new ServiceResponse
            {
                Success = true,
                Message = "capture-ready",
                TcpRedirectPort = router.TcpRedirectPort,
                UdpRedirectPort = router.UdpRedirectPort,
            }).ConfigureAwait(false);

            while (true)
            {
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ControlRequest? control = JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.ControlRequest);
                if (string.Equals(control?.Command, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await SendAsync(writer, Success("capture-stopping")).ConfigureAwait(false);
                    break;
                }
            }

            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static ServiceResponse Success(string message) => new() { Success = true, Message = message };
    private static ServiceResponse Failure(string message) => new() { Success = false, Message = message };

    private static Task SendAsync(StreamWriter writer, ServiceResponse response)
        => writer.WriteLineAsync(JsonSerializer.Serialize(response, NetworkServiceJsonContext.Default.ServiceResponse));

    private static void TryWriteStartupFailure(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, message, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private sealed class CaptureChild : IAsyncDisposable
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
            int udpRedirectPort)
        {
            _process = process;
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
            TcpRedirectPort = tcpRedirectPort;
            UdpRedirectPort = udpRedirectPort;
        }

        public int TcpRedirectPort { get; }
        public int UdpRedirectPort { get; }

        public static async Task<CaptureChild> StartAsync(StartRequest request)
        {
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
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"--capture-pipe {pipeName}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                }) ?? throw new InvalidOperationException("Unable to start WinDivert capture child.");

                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                request.Command = "start";
                await writer.WriteLineAsync(JsonSerializer.Serialize(request, NetworkServiceJsonContext.Default.StartRequest)).ConfigureAwait(false);
                string? line = await reader.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15))
                    .ConfigureAwait(false);
                ServiceResponse? response = string.IsNullOrWhiteSpace(line)
                    ? null
                    : JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.ServiceResponse);
                if (response?.Success != true)
                {
                    throw new InvalidOperationException(response?.Message ?? "WinDivert capture child failed to start.");
                }

                return new CaptureChild(process, pipe, reader, writer, response.TcpRedirectPort, response.UdpRedirectPort);
            }
            catch
            {
                writer?.Dispose();
                reader?.Dispose();
                pipe.Dispose();
                if (process is not null)
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
                    Task<string?> readTask = _reader.ReadLineAsync();
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
    }
}
