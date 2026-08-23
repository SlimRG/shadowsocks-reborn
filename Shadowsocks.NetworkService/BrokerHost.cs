using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService;

internal static class BrokerHost
{
    public static async Task<int> RunAsync(string pipeName, string? startupLogPath)
    {
        await using var supervisor = new CaptureChildSupervisor();
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
                    await NetworkServiceResponses.SendAsync(
                        writer,
                        NetworkServiceResponses.Failure("Invalid control request: " + exception.Message)).ConfigureAwait(false);
                    continue;
                }

                string command = control?.Command ?? string.Empty;
                ServiceResponse response;
                if (command.Equals("start", StringComparison.OrdinalIgnoreCase)
                    || command.Equals("restart", StringComparison.OrdinalIgnoreCase))
                {
                    StartRequest? request = JsonSerializer.Deserialize(line, NetworkServiceJsonContext.Default.StartRequest);
                    response = request is null
                        ? NetworkServiceResponses.Failure("Invalid capture configuration.")
                        : await supervisor.StartOrRestartAsync(request).ConfigureAwait(false);
                }
                else if (command.Equals("game-on", StringComparison.OrdinalIgnoreCase))
                {
                    response = await supervisor.EnterGameModeAsync().ConfigureAwait(false);
                }
                else if (command.Equals("game-off", StringComparison.OrdinalIgnoreCase))
                {
                    response = await supervisor.ExitGameModeAsync().ConfigureAwait(false);
                }
                else if (command.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    response = await supervisor.PingAsync().ConfigureAwait(false);
                }
                else if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    response = await supervisor.StopAsync().ConfigureAwait(false);
                    await NetworkServiceResponses.SendAsync(writer, response).ConfigureAwait(false);
                    break;
                }
                else
                {
                    response = NetworkServiceResponses.Failure("Unknown command.");
                }

                await NetworkServiceResponses.SendAsync(writer, response).ConfigureAwait(false);
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
    }

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
}
