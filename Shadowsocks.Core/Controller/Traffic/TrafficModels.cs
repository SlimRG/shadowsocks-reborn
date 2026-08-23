using System.Collections.Generic;
using System.Net;

namespace Shadowsocks.Controller.Traffic
{
    public enum TrafficRouteAction
    {
        Default = 0,
        Proxy = 1,
        Direct = 2,
        Block = 3,
    }

    public enum TrafficCaptureMode
    {
        User = 0,
        Admin = 1,
    }

    public enum TrafficRuntimeMode
    {
        User = 0,
        Admin = 1,
        Game = 2,
    }

    public enum DnsPolicyMode
    {
        System = 0,
        Direct = 1,
        Proxy = 2,
        CustomDoh = 3,
        DnsCrypt = 4,
    }

    public sealed record ManagedRoutingStatus(
        string EffectiveMode,
        string SnapshotMode,
        long Generation,
        bool Enabled,
        string Source,
        System.DateTime CreatedUtc,
        int DefaultRuleCount,
        int UserRuleCount,
        int InvalidRuleCount,
        long DirectDecisionCount,
        long ProxyDecisionCount,
        bool AdminManagedRoutingActive,
        int AdminManagedRoutingRuleCount);

    public sealed record TrafficCaptureStatus(
        TrafficCaptureMode ConfiguredMode,
        TrafficRuntimeMode RuntimeMode,
        bool NetworkServiceRunning,
        bool WinDivertActive,
        bool GameModeActive,
        bool TcpCaptureActive,
        bool UdpCaptureActive,
        bool DnsInterceptionActive,
        bool DnsFailClosedActive,
        int TcpRedirectPort,
        int UdpRedirectPort,
        IReadOnlyList<string> RunningGameApplications);

    /// <summary>
    /// Ephemeral DNSCrypt runtime data passed to the elevated capture service.
    /// This state is intentionally not persisted in settings because the listener
    /// port and process identifier change whenever DNSCrypt restarts.
    /// </summary>
    public readonly record struct DnsCaptureRuntimeState(int Port, int ProcessId)
    {
        public bool IsReady => Port is >= 1 and <= 65535 && ProcessId > 0;
        public static DnsCaptureRuntimeState Unavailable => default;
    }

    public sealed class TrafficContext
    {
        public int ProcessId { get; init; }
        public string ProcessPath { get; init; }
        public string ProcessName { get; init; }
        public string Protocol { get; init; }
        public string DestinationHost { get; init; }
        public string DestinationUrl { get; init; }
        public IPAddress DestinationAddress { get; init; }
        public int DestinationPort { get; init; }
        public bool IsInternal { get; init; }
    }

    public readonly record struct RouteDecision(TrafficRouteAction Action, string Reason)
    {
        public static RouteDecision Proxy(string reason) => new(TrafficRouteAction.Proxy, reason);
        public static RouteDecision Direct(string reason) => new(TrafficRouteAction.Direct, reason);
        public static RouteDecision Block(string reason) => new(TrafficRouteAction.Block, reason);
    }
}
