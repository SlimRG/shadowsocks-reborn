using System.Collections.Concurrent;
using System.Net;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class FlowRegistry
{
    private readonly ConcurrentDictionary<FlowKey, FlowState> _flows = new();
    private readonly ConcurrentDictionary<ReflectedFlowKey, FlowState> _reflected = new();
    private readonly ApplicationPolicy _policy;
    private long _lastSweepTicks;

    public FlowRegistry(ApplicationPolicy policy)
    {
        _policy = policy;
    }

    public FlowState GetOrCreate(FlowKey key)
    {
        FlowState state = _flows.GetOrAdd(key, CreateState);
        DateTime now = DateTime.UtcNow;
        if ((now - state.LastSeenUtc) > TimeSpan.FromSeconds(1))
        {
            FlowState updated = state with { LastSeenUtc = now };
            _flows.TryUpdate(key, updated, state);
            state = updated;
            if (state.Route == RouteAction.Proxy)
            {
                _reflected[ToReflectedKey(key)] = state;
            }
        }

        MaybeSweep(now);
        return state;
    }

    public bool TryGetReflected(byte protocol, IPAddress localAddress, ushort applicationPort, IPAddress remoteAddress, out FlowState? state)
    {
        ReflectedFlowKey key = new(protocol, localAddress, applicationPort, remoteAddress);
        if (_reflected.TryGetValue(key, out state))
        {
            return true;
        }

        // UDP sockets bound to ANY may cause the transparent relay's reply to choose a
        // different local interface address. Local source ports remain sufficient to
        // disambiguate normal Windows socket usage; keep this as a multi-homed fallback.
        foreach ((ReflectedFlowKey candidate, FlowState value) in _reflected)
        {
            if (candidate.Protocol == protocol
                && candidate.ApplicationPort == applicationPort
                && candidate.RemoteAddress.Equals(remoteAddress))
            {
                state = value;
                return true;
            }
        }

        state = null;
        return false;
    }

    public bool TryGetByPeer(byte protocol, IPAddress remoteAddress, ushort applicationPort, out FlowState? state)
    {
        foreach ((ReflectedFlowKey key, FlowState value) in _reflected)
        {
            if (key.Protocol == protocol
                && key.ApplicationPort == applicationPort
                && key.RemoteAddress.Equals(remoteAddress))
            {
                state = value;
                return true;
            }
        }

        state = null;
        return false;
    }

    private FlowState CreateState(FlowKey key)
    {
        ProcessIdentity process = ProcessResolver.Resolve(key);
        RouteAction route = _policy.Evaluate(process.ProcessId, process.ProcessPath, process.ProcessName);
        FlowState state = new(key, process.ProcessId, process.ProcessPath, process.ProcessName, route, DateTime.UtcNow);
        if (route == RouteAction.Proxy)
        {
            _reflected[ToReflectedKey(key)] = state;
        }

        return state;
    }

    private static ReflectedFlowKey ToReflectedKey(FlowKey key)
        => new(key.Protocol, key.LocalAddress, key.LocalPort, key.RemoteAddress);

    private void MaybeSweep(DateTime now)
    {
        long ticks = now.Ticks;
        long previous = Interlocked.Read(ref _lastSweepTicks);
        if (ticks - previous < TimeSpan.FromSeconds(30).Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, ticks, previous) != previous)
        {
            return;
        }

        foreach ((FlowKey key, FlowState state) in _flows)
        {
            TimeSpan timeout = key.Protocol == 17 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(15);
            if (now - state.LastSeenUtc <= timeout)
            {
                continue;
            }

            _flows.TryRemove(key, out _);
            _reflected.TryRemove(ToReflectedKey(key), out _);
        }
    }
}
