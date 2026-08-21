using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Core.Storage;
using Shadowsocks.Util.ProcessManagement;

namespace Shadowsocks.Controller.Service
{
    // https://github.com/shadowsocks/shadowsocks-org/wiki/Plugin
    public sealed class Sip003Plugin : IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public IPEndPoint LocalEndPoint { get; private set; }
        public int ProcessId => _started ? _pluginProcess.Id : 0;

        private readonly object _startProcessLock = new object();
        private readonly Job _pluginJob;
        private readonly Process _pluginProcess;
        private readonly Func<bool> _showPluginOutputProvider;
        private bool _started;
        private bool _disposed;

        public static Sip003Plugin CreateIfConfigured(Server server, bool showPluginOutput)
            => CreateIfConfigured(server, () => showPluginOutput);

        public static Sip003Plugin CreateIfConfigured(Server server, Func<bool> showPluginOutputProvider)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            if (string.IsNullOrWhiteSpace(server.plugin))
            {
                return null;
            }

            return new Sip003Plugin(
                server.plugin,
                server.plugin_opts,
                server.plugin_args,
                server.server,
                server.server_port,
                showPluginOutputProvider);
        }

        private Sip003Plugin(string plugin, string pluginOpts, string pluginArgs, string serverAddress, int serverPort, Func<bool> showPluginOutputProvider)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            _showPluginOutputProvider = showPluginOutputProvider ?? (() => false);
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(serverAddress));
            }
            if (serverPort <= 0 || serverPort > 65535)
            {
                throw new ArgumentOutOfRangeException("serverPort");
            }

            string resolvedPlugin = PluginManager.ResolveExecutable(plugin);
            if (string.IsNullOrWhiteSpace(resolvedPlugin))
            {
                throw new FileNotFoundException(I18N.GetString("Cannot find the plugin program file"), plugin);
            }
            string pluginWorkingDirectory = AppStoragePaths.EnsureTempDirectory(
                Path.Combine(AppStoragePaths.TempWorkingRoot, "Plugins"));

            _pluginProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = resolvedPlugin,
                    Arguments = pluginArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ErrorDialog = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = pluginWorkingDirectory,
                    Environment =
                    {
                        ["SS_REMOTE_HOST"] = serverAddress,
                        ["SS_REMOTE_PORT"] = serverPort.ToString(),
                        ["SS_PLUGIN_OPTIONS"] = pluginOpts
                    }
                }
            };
            _pluginProcess.OutputDataReceived += OnPluginOutputDataReceived;
            _pluginProcess.ErrorDataReceived += OnPluginErrorDataReceived;

            _pluginJob = new Job();
        }

        public bool StartIfNeeded()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            lock (_startProcessLock)
            {
                if (_started && !_pluginProcess.HasExited)
                {
                    return false;
                }

                var localPort = GetNextFreeTcpPort();
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);

                _pluginProcess.StartInfo.Environment["SS_LOCAL_HOST"] = LocalEndPoint.Address.ToString();
                _pluginProcess.StartInfo.Environment["SS_LOCAL_PORT"] = LocalEndPoint.Port.ToString();
                _pluginProcess.StartInfo.Arguments = ExpandEnvironmentVariables(_pluginProcess.StartInfo.Arguments, _pluginProcess.StartInfo.EnvironmentVariables);
                try
                {
                    _pluginProcess.Start();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 0x00000002)
                    {
                        throw new FileNotFoundException(I18N.GetString("Cannot find the plugin program file"), _pluginProcess.StartInfo.FileName, ex);
                    }
                    throw new ApplicationException(I18N.GetString("Plugin Program"), ex);
                }
                _pluginProcess.BeginOutputReadLine();
                _pluginProcess.BeginErrorReadLine();
                _pluginJob.AddProcess(_pluginProcess.Handle);
                _started = true;
            }

            return true;
        }

        private void OnPluginOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && IsPluginOutputEnabled())
                Logger.Info("SIP003 | STDOUT | {0}", e.Data);
        }

        private void OnPluginErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && IsPluginOutputEnabled())
                Logger.Warn("SIP003 | STDERR | {0}", e.Data);
        }

        private bool IsPluginOutputEnabled()
        {
            try { return _showPluginOutputProvider(); }
            catch { return false; }
        }

        public string ExpandEnvironmentVariables(string name, StringDictionary environmentVariables = null)
        {
            // Expand the environment variables from the new process itself
            if (environmentVariables != null)
            {
                foreach (string key in environmentVariables.Keys)
                {
                    name = name.Replace($"%{key}%", environmentVariables[key]);
                }
            }
            // Also expand the environment variables from current main process (system)
            name = Environment.ExpandEnvironmentVariables(name);
            return name;
        }

        static int GetNextFreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_pluginProcess.HasExited)
                {
                    _pluginProcess.Kill();
                    _pluginProcess.WaitForExit(1500);
                }
            }
            catch (Exception) { }
            finally
            {
                try
                {
                    _pluginProcess.OutputDataReceived -= OnPluginOutputDataReceived;
                    _pluginProcess.ErrorDataReceived -= OnPluginErrorDataReceived;
                    _pluginProcess.Dispose();
                    _pluginJob.Dispose();
                }
                catch (Exception) { }

                _disposed = true;
            }
        }
    }
}
