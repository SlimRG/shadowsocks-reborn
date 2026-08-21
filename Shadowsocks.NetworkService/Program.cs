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

        return await BrokerHost.RunAsync(pipeName, startupLogPath).ConfigureAwait(false);
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
                await NetworkServiceResponses.SendAsync(
                    writer,
                    NetworkServiceResponses.Failure("Invalid capture child request.")).ConfigureAwait(false);
                return 3;
            }

            await using WinDivertTransparentRouter router = new(request);
            try
            {
                router.Start();
            }
            catch (Exception exception)
            {
                await NetworkServiceResponses.SendAsync(
                    writer,
                    NetworkServiceResponses.Failure(exception.Message)).ConfigureAwait(false);
                return 5;
            }

            await NetworkServiceResponses.SendAsync(writer, new ServiceResponse
            {
                Success = true,
                Message = "capture-ready",
                TcpRedirectPort = router.TcpRedirectPort,
                UdpRedirectPort = router.UdpRedirectPort,
                CaptureActive = true,
                DnsInterceptionActive = router.DnsInterceptionActive,
                DnsFailClosedActive = router.DnsFailClosedActive,
            }).ConfigureAwait(false);

            while (true)
            {
                Task<string?> readTask = reader.ReadLineAsync();
                Task completed = await Task.WhenAny(readTask, router.Completion).ConfigureAwait(false);
                if (completed == router.Completion)
                {
                    await router.Completion.ConfigureAwait(false);
                    return 6;
                }

                string? line = await readTask.ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                ControlRequest? control = JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.ControlRequest);
                if (string.Equals(control?.Command, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    await NetworkServiceResponses.SendAsync(
                        writer,
                        NetworkServiceResponses.Success("capture-stopping")).ConfigureAwait(false);
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
}
