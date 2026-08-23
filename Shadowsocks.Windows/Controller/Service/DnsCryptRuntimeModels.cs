#nullable enable
using System;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.Controller.Service
{
    public enum DnsCryptRuntimeState
    {
        NotInstalled = 0,
        Stopped = 1,
        Starting = 2,
        Running = 3,
        Updating = 4,
        Failed = 5,
    }

    public sealed record DnsCryptRuntimeStatus(
        DnsCryptRuntimeState State,
        Version? Version,
        int ProcessId,
        int Port,
        DateTimeOffset? StartedAtUtc,
        string? ConfigPath,
        string? LastError)
    {
        public bool IsRunning => State == DnsCryptRuntimeState.Running;
        public bool IsServing => ProcessId > 0 && (State == DnsCryptRuntimeState.Running || State == DnsCryptRuntimeState.Updating);
    }

    public sealed record DnsCryptRuntimeStartOptions(
        DnsCryptConfig Config,
        int? ShadowsocksSocks5Port = null)
    {
        public string ShadowsocksSocks5Host { get; init; } = "127.0.0.1";

        public System.Collections.Generic.IReadOnlyDictionary<string, string> StaticResolverStamps { get; init; }
            = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record DnsCryptResolverInfo(
        string Name,
        string Protocol,
        bool IPv6,
        bool? Dnssec,
        bool NoLog,
        bool NoFilter,
        string Description,
        System.Collections.Generic.IReadOnlyList<string> Addresses,
        string CountryCode = "",
        string CountryName = "",
        string FlagEmoji = "",
        int? LatencyMs = null)
    {
        public System.Collections.Generic.IReadOnlyList<int> Ports { get; init; } = System.Array.Empty<int>();
        public string Stamp { get; init; } = string.Empty;
    }

    public sealed class DnsCryptBootstrapException : InvalidOperationException
    {
        public DnsCryptBootstrapException() { }
        public DnsCryptBootstrapException(string message) : base(message) { }
        public DnsCryptBootstrapException(string message, Exception? innerException) : base(message, innerException) { }
    }

    public sealed class DnsCryptComponentNetworkException : InvalidOperationException
    {
        public DnsCryptComponentNetworkException() { }
        public DnsCryptComponentNetworkException(string message) : base(message) { }
        public DnsCryptComponentNetworkException(string message, Exception? innerException) : base(message, innerException) { }
    }

    public sealed class DnsCryptRuntimeStartupException : InvalidOperationException
    {
        public DnsCryptRuntimeStartupException() { }
        public DnsCryptRuntimeStartupException(string message) : base(message) { }
        public DnsCryptRuntimeStartupException(string message, Exception? innerException) : base(message, innerException) { }
    }

    public sealed class DnsCryptRuntimeStatusChangedEventArgs : EventArgs
    {
        public DnsCryptRuntimeStatusChangedEventArgs(DnsCryptRuntimeStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            Status = status;
        }

        public DnsCryptRuntimeStatus Status { get; }
    }

    internal sealed record DnsCryptCommandResult(int ExitCode, string StandardOutput, string StandardError);

    internal interface IDnsCryptRunningProcess : IDisposable
    {
        int Id { get; }
        bool HasExited { get; }
        event EventHandler? Exited;
        void Kill();
        System.Threading.Tasks.Task WaitForExitAsync(System.Threading.CancellationToken cancellationToken);
    }

    internal interface IDnsCryptRuntimePlatform
    {
        System.Threading.Tasks.Task<DnsCryptCommandResult> ExecuteAsync(
            string executablePath,
            string workingDirectory,
            System.Collections.Generic.IReadOnlyList<string> arguments,
            TimeSpan timeout,
            System.Threading.CancellationToken cancellationToken);

        IDnsCryptRunningProcess Start(
            string executablePath,
            string workingDirectory,
            System.Collections.Generic.IReadOnlyList<string> arguments,
            Action<string> standardOutput,
            Action<string> standardError);
    }
}
