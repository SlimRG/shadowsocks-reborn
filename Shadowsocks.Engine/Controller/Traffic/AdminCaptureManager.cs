using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Traffic
{
    internal sealed class AdminElevationCanceledException(string message, Exception innerException)
        : OperationCanceledException(message, innerException)
    {
    }

    /// <summary>
    /// Owns the elevated WinDivert broker. The broker is intentionally separate from
    /// the UI process. In Game Mode it terminates its capture child and removes the
    /// WinDivert driver while keeping the elevated broker alive, allowing Admin Mode
    /// to resume later without another UAC prompt.
    /// </summary>
    internal sealed class AdminCaptureManager : IAsyncDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly WinDivertInstaller _installer = new();

        private Process _brokerProcess;
        private NamedPipeServerStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _lastStartRequest;
        private bool _captureActive;
        private bool _gameMode;
        private bool _disposed;

        public bool IsBrokerRunning => _brokerProcess is { HasExited: false } && _pipe is { IsConnected: true };
        public bool IsCaptureActive => _captureActive && IsBrokerRunning;
        public bool IsGameMode => _gameMode;

        public event EventHandler StatusChanged;

        public async Task StartOrUpdateAsync(
            Configuration configuration,
            IEnumerable<int> excludedProcessIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                string winDivertDirectory = await _installer
                    .EnsureInstalledAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);
                _lastStartRequest = BuildStartRequest(configuration, winDivertDirectory, excludedProcessIds);

                await EnsureBrokerAsync(cancellationToken).ConfigureAwait(false);
                if (_gameMode)
                {
                    // The configuration is remembered by the broker, but no driver/capture
                    // process is started until Game Mode ends.
                    JObject gameModeResponse = await SendRawAsync(
                        BuildControlRequest("restart", configuration, winDivertDirectory, excludedProcessIds),
                        cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(gameModeResponse, "Unable to update Admin Mode while Game Mode is active.");
                    _captureActive = false;
                    RaiseStatusChanged();
                    return;
                }

                string command = IsCaptureActive ? "restart" : "start";
                try
                {
                    JObject response = await SendRawAsync(
                        command == "start"
                            ? _lastStartRequest
                            : BuildControlRequest("restart", configuration, winDivertDirectory, excludedProcessIds),
                        cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(response, "Unable to activate Admin Mode.");
                    LogCaptureConfirmed(response, command);
                    _captureActive = true;
                }
                catch
                {
                    _captureActive = false;
                    RaiseStatusChanged();
                    throw;
                }

                RaiseStatusChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task EnterGameModeAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsBrokerRunning)
                {
                    JObject response = await SendCommandAsync("game-on", cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(response, "Unable to disable WinDivert for Game Mode.");
                }

                _gameMode = true;
                _captureActive = false;
                RaiseStatusChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task ExitGameModeAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!IsBrokerRunning || string.IsNullOrWhiteSpace(_lastStartRequest))
                {
                    _gameMode = false;
                    _captureActive = false;
                    RaiseStatusChanged();
                    return;
                }

                JObject response = await SendCommandAsync("game-off", cancellationToken).ConfigureAwait(false);
                EnsureSuccess(response, "Unable to restore Admin Mode after Game Mode.");
                LogCaptureConfirmed(response, "game-off");
                _gameMode = false;
                _captureActive = true;
                RaiseStatusChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                if (IsBrokerRunning)
                {
                    try
                    {
                        await SendCommandAsync("stop", cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        Logger.Debug(exception, "Elevated network broker did not stop cleanly.");
                    }
                }

                CleanupBroker();
                _captureActive = false;
                _gameMode = false;
                RaiseStatusChanged();
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureBrokerAsync(CancellationToken cancellationToken)
        {
            if (IsBrokerRunning)
            {
                return;
            }

            CleanupBroker();
            string helperPath = FindHelperPath();
            string pipeName = "Shadowsocks.NetworkService." + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough);

            ProcessStartInfo startInfo = new()
            {
                FileName = helperPath,
                Arguments = $"--pipe {pipeName}",
                WorkingDirectory = Path.GetDirectoryName(helperPath) ?? Shadowsocks.Engine.RuntimeEnvironment.WorkingDirectory,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            ValidateFrameworkDependentHelperFiles(helperPath);

            string startupLogPath = Path.Combine(
                Path.GetTempPath(),
                $"Shadowsocks.NetworkService.{pipeName}.startup.log");
            startInfo.Arguments += $" --startup-log \"{startupLogPath}\"";

            try
            {
                _brokerProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Unable to start the elevated network broker.");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                CleanupBroker();
                throw new AdminElevationCanceledException("Administrator elevation was cancelled.", exception);
            }

            try
            {
                Task connectTask = _pipe.WaitForConnectionAsync(cancellationToken);
                Task exitTask = _brokerProcess.WaitForExitAsync(cancellationToken);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                Task completed = await Task.WhenAny(connectTask, exitTask, timeoutTask).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (completed == connectTask)
                {
                    await connectTask.ConfigureAwait(false);
                }
                else if (completed == exitTask)
                {
                    await exitTask.ConfigureAwait(false);
                    int exitCode = _brokerProcess.ExitCode;
                    string detail = ReadStartupFailure(startupLogPath);
                    const int appPathFindFailure = unchecked((int)0x8000809A);
                    if (exitCode == appPathFindFailure)
                    {
                        throw new InvalidOperationException(
                            "Shadowsocks.NetworkService.exe is a framework-dependent .NET apphost, but its " +
                            "Shadowsocks.NetworkService.dll is not beside it (host error 0x8000809A). " +
                            "This usually means a stale framework-dependent helper was copied into the publish directory. " +
                            "Republish the application with the current x64 profile.");
                    }

                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(detail)
                            ? $"Elevated network broker exited before connecting to the control pipe (exit code {exitCode}). " +
                              "For a framework-dependent Debug build, Shadowsocks.NetworkService.runtimeconfig.json, .deps.json and .dll must be beside the helper executable."
                            : $"Elevated network broker exited before connecting to the control pipe (exit code {exitCode}). {detail}");
                }
                else
                {
                    string detail = ReadStartupFailure(startupLogPath);
                    throw new TimeoutException(
                        string.IsNullOrWhiteSpace(detail)
                            ? "Timed out waiting for the elevated network broker to connect to the control pipe."
                            : $"Timed out waiting for the elevated network broker to connect to the control pipe. {detail}");
                }
            }
            catch
            {
                CleanupBroker();
                throw;
            }
            finally
            {
                TryDelete(startupLogPath);
            }

            _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true)
            {
                AutoFlush = true,
            };
        }

        private async Task<JObject> SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            return await SendRawAsync(
                JsonConvert.SerializeObject(new { command }),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<JObject> SendRawAsync(string json, CancellationToken cancellationToken)
        {
            if (!IsBrokerRunning || _reader is null || _writer is null)
            {
                throw new InvalidOperationException("Elevated network broker is not connected.");
            }

            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            Task<string> readTask = _reader.ReadLineAsync();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            Task completed = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);
            if (completed != readTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CleanupBroker();
                _captureActive = false;
                throw new TimeoutException("Timed out waiting for the elevated network broker.");
            }

            string line = await readTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                CleanupBroker();
                throw new IOException("Elevated network broker disconnected unexpectedly.");
            }

            try
            {
                return JObject.Parse(line);
            }
            catch (JsonReaderException)
            {
                CleanupBroker();
                throw;
            }
        }

        private static string BuildControlRequest(
            string command,
            Configuration configuration,
            string winDivertDirectory,
            IEnumerable<int> excludedProcessIds)
        {
            JObject request = JObject.Parse(BuildStartRequest(configuration, winDivertDirectory, excludedProcessIds));
            request["command"] = command;
            return request.ToString(Formatting.None);
        }

        private static string BuildStartRequest(
            Configuration configuration,
            string winDivertDirectory,
            IEnumerable<int> excludedProcessIds)
        {
            int fallback = configuration.enabled && configuration.global
                ? (int)TrafficRouteAction.Proxy
                : (int)TrafficRouteAction.Direct;
            var rules = (configuration.applicationRules ?? [])
                .Where(rule => rule is not null)
                .Select(rule => new
                {
                    enabled = rule.enabled,
                    application = rule.application ?? string.Empty,
                    action = (int)rule.action,
                })
                .ToArray();

            var request = new
            {
                command = "start",
                winDivertDirectory,
                localProxyHost = NormalizeLoopbackHost(configuration.LocalHost),
                localProxyPort = configuration.localPort,
                mainProcessId = Environment.ProcessId,
                defaultRoute = fallback,
                applicationRules = rules,
                excludedProcessIds = (excludedProcessIds ?? [])
                    .Where(processId => processId > 0)
                    .Distinct()
                    .ToArray(),
                dnsPolicy = new
                {
                    mode = (int)(configuration.dnsPolicy?.mode ?? DnsPolicyMode.System),
                    customDohUrl = configuration.dnsPolicy?.customDohUrl ?? string.Empty,
                },
            };
            return JsonConvert.SerializeObject(request);
        }

        private static string NormalizeLoopbackHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return "127.0.0.1";
            }

            return host.Trim().TrimStart('[').TrimEnd(']');
        }

        private static void EnsureSuccess(JObject response, string prefix)
        {
            if (response.Value<bool?>("success") == true)
            {
                return;
            }

            string message = response.Value<string>("message");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? prefix : $"{prefix} {message}");
        }

        private static void ValidateFrameworkDependentHelperFiles(string helperPath)
        {
            string directory = Path.GetDirectoryName(helperPath) ?? Shadowsocks.Engine.RuntimeEnvironment.WorkingDirectory;
            string baseName = Path.GetFileNameWithoutExtension(helperPath);
            string managedDll = Path.Combine(directory, baseName + ".dll");
            if (!File.Exists(managedDll))
            {
                // Product single-file publishing intentionally has no managed sidecars.
                return;
            }

            string[] required =
            [
                managedDll,
                Path.Combine(directory, baseName + ".deps.json"),
                Path.Combine(directory, baseName + ".runtimeconfig.json"),
            ];
            string[] missing = required.Where(path => !File.Exists(path)).ToArray();
            if (missing.Length > 0)
            {
                throw new FileNotFoundException(
                    "The framework-dependent Shadowsocks.NetworkService build is incomplete. Missing: " +
                    string.Join(", ", missing.Select(Path.GetFileName)) +
                    ". Rebuild the complete solution or publish the NetworkService product helper.",
                    missing[0]);
            }
        }

        private static string ReadStartupFailure(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }

        private static string FindHelperPath()
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "Shadowsocks.NetworkService.exe"),
                Path.Combine(Shadowsocks.Engine.RuntimeEnvironment.WorkingDirectory, "Shadowsocks.NetworkService.exe"),
            ];

            string path = candidates.FirstOrDefault(File.Exists);
            if (path is null)
            {
                throw new FileNotFoundException(
                    "Shadowsocks.NetworkService.exe was not found. Build the complete solution before enabling Admin Mode.",
                    candidates[0]);
            }

            return path;
        }

        private void CleanupBroker()
        {
            _captureActive = false;

            try
            {
                _writer?.Dispose();
                _reader?.Dispose();
                _pipe?.Dispose();
            }
            catch
            {
            }

            _writer = null;
            _reader = null;
            _pipe = null;

            if (_brokerProcess is not null)
            {
                try
                {
                    if (!_brokerProcess.HasExited)
                    {
                        _brokerProcess.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                _brokerProcess.Dispose();
                _brokerProcess = null;
            }
        }

        private static void LogCaptureConfirmed(JObject response, string operation)
        {
            int tcpPort = response.Value<int?>("tcpRedirectPort") ?? 0;
            int udpPort = response.Value<int?>("udpRedirectPort") ?? 0;
            string message = response.Value<string>("message") ?? "capture-ready";
            Logger.Info(
                "WinDivert capture confirmed ({0}): {1}; TCP redirect port={2}, UDP redirect port={3}. " +
                "The elevated capture child returned success only after WinDivertOpen completed.",
                operation,
                message,
                tcpPort,
                udpPort);
        }

        private void RaiseStatusChanged()
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await StopAsync().ConfigureAwait(false);
            _disposed = true;
            _gate.Dispose();
        }
    }
}
