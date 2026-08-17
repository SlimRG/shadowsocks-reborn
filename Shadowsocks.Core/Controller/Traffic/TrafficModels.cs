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
    }

    public sealed record TrafficCaptureStatus(
        TrafficCaptureMode ConfiguredMode,
        TrafficRuntimeMode RuntimeMode,
        bool NetworkServiceRunning,
        bool WinDivertActive,
        bool GameModeActive,
        bool TcpCaptureActive,
        bool UdpCaptureActive,
        int TcpRedirectPort,
        int UdpRedirectPort,
        IReadOnlyList<string> RunningGameApplications);

    public sealed class TrafficContext
    {
        public int ProcessId { get; init; }
        public string ProcessPath { get; init; }
        public string ProcessName { get; init; }
        public string Protocol { get; init; }
        public string DestinationHost { get; init; }
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
