using System.Net;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal readonly record struct FlowKey(
    byte Protocol,
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort);

internal readonly record struct ReflectedFlowKey(
    byte Protocol,
    IPAddress LocalAddress,
    ushort ApplicationPort,
    IPAddress RemoteAddress);

internal readonly record struct PeerFlowKey(
    byte Protocol,
    ushort ApplicationPort,
    IPAddress RemoteAddress);

internal sealed record FlowState(
    FlowKey Key,
    int ProcessId,
    string? ProcessPath,
    string? ProcessName,
    RouteAction Route,
    DateTime LastSeenUtc);
