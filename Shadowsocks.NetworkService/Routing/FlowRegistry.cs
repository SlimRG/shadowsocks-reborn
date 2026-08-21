using System.Collections.Concurrent;
using System.Net;
using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class FlowRegistry
{
    private readonly ConcurrentDictionary<FlowKey, FlowState> _flows = new();
    private readonly ConcurrentDictionary<ReflectedFlowKey, FlowState> _reflected = new();
    private readonly ConcurrentDictionary<PeerFlowKey, ConcurrentDictionary<ReflectedFlowKey, FlowState>> _reflectedByPeer = new();
    private readonly ApplicationPolicy _policy;
    private long _lastSweepTicks;

    public FlowRegistry(ApplicationPolicy policy)
    {
        _policy = policy;
    }

    public FlowState GetOrCreate(FlowKey key)
    {
        FlowState state = _flows.GetOrAdd(key, CreateState);
        if (state.ProcessId <= 0 && key.RemotePort == 53)
        {
            ProcessIdentity process = ProcessResolver.Resolve(key);
            if (process.ProcessId > 0)
            {
                FlowState resolved = state with
                {
                    ProcessId = process.ProcessId,
                    ProcessPath = process.ProcessPath,
                    ProcessName = process.ProcessName,
                    Route = _policy.Evaluate(process.ProcessId, process.ProcessPath, process.ProcessName),
                };
                if (_flows.TryUpdate(key, resolved, state))
                {
                    state = resolved;
                    if (resolved.Route == RouteAction.Proxy)
                    {
                        IndexReflected(resolved);
                    }
                    else
                    {
                        RemoveReflected(key);
                    }
                }
                else if (_flows.TryGetValue(key, out FlowState? current) && current is not null)
                {
                    state = current;
                }
            }
        }

        DateTime now = DateTime.UtcNow;
        if ((now - state.LastSeenUtc) > TimeSpan.FromSeconds(1))
        {
            FlowState updated = state with { LastSeenUtc = now };
            if (_flows.TryUpdate(key, updated, state))
            {
                state = updated;
                if (state.Route == RouteAction.Proxy)
                {
                    IndexReflected(state);
                }
            }
            else if (_flows.TryGetValue(key, out FlowState? current) && current is not null)
            {
                state = current;
            }
        }

        MaybeSweep(now);
        return state;
    }

    public bool IsExcludedProcess(FlowState state)
        => state is not null && _policy.IsExcludedProcess(state.ProcessId, state.ProcessPath);

    public bool IsDnsInterceptionExempt(FlowState state)
        => state is not null && _policy.IsDnsInterceptionExempt(state.ProcessId, state.ProcessPath);

    public void MarkReflected(FlowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IndexReflected(state);
    }

    public bool TryGetReflected(
        byte protocol,
        IPAddress localAddress,
        ushort applicationPort,
        IPAddress remoteAddress,
        out FlowState? state)
    {
        ReflectedFlowKey exactKey = new(protocol, localAddress, applicationPort, remoteAddress);
        if (_reflected.TryGetValue(exactKey, out state))
        {
            return true;
        }

        // UDP sockets bound to ANY may cause the transparent relay reply to use a
        // different local interface. Keep an O(1) peer index for that fallback.
        return TryGetByPeer(protocol, remoteAddress, applicationPort, out state);
    }

    public bool TryGetByPeer(
        byte protocol,
        IPAddress remoteAddress,
        ushort applicationPort,
        out FlowState? state)
    {
        PeerFlowKey peerKey = new(protocol, applicationPort, remoteAddress);
        if (!_reflectedByPeer.TryGetValue(peerKey, out ConcurrentDictionary<ReflectedFlowKey, FlowState>? bucket)
            || bucket.IsEmpty)
        {
            state = null;
            return false;
        }

        // More than one local interface can legitimately produce the same peer tuple.
        // Keep those rare collisions inside a small peer bucket instead of scanning every flow.
        state = bucket.Values.OrderByDescending(candidate => candidate.LastSeenUtc).FirstOrDefault();
        return state is not null;
    }

    private FlowState CreateState(FlowKey key)
    {
        ProcessIdentity process = ProcessResolver.Resolve(key);
        RouteAction route = _policy.Evaluate(process.ProcessId, process.ProcessPath, process.ProcessName);
        FlowState state = new(key, process.ProcessId, process.ProcessPath, process.ProcessName, route, DateTime.UtcNow);
        if (route == RouteAction.Proxy)
        {
            IndexReflected(state);
        }

        return state;
    }

    private static ReflectedFlowKey ToReflectedKey(FlowKey key)
        => new(key.Protocol, key.LocalAddress, key.LocalPort, key.RemoteAddress);

    private static PeerFlowKey ToPeerKey(FlowKey key)
        => new(key.Protocol, key.LocalPort, key.RemoteAddress);

    private void IndexReflected(FlowState state)
    {
        ReflectedFlowKey reflectedKey = ToReflectedKey(state.Key);
        PeerFlowKey peerKey = ToPeerKey(state.Key);
        _reflected[reflectedKey] = state;
        ConcurrentDictionary<ReflectedFlowKey, FlowState> bucket = _reflectedByPeer.GetOrAdd(
            peerKey,
            static _ => new ConcurrentDictionary<ReflectedFlowKey, FlowState>());
        bucket[reflectedKey] = state;
    }

    private void RemoveReflected(FlowKey key)
    {
        ReflectedFlowKey reflectedKey = ToReflectedKey(key);
        PeerFlowKey peerKey = ToPeerKey(key);
        _reflected.TryRemove(reflectedKey, out _);
        if (_reflectedByPeer.TryGetValue(peerKey, out ConcurrentDictionary<ReflectedFlowKey, FlowState>? bucket))
        {
            bucket.TryRemove(reflectedKey, out _);
            RemovePeerBucketIfEmpty(peerKey, bucket);
        }
    }

    private void RemoveReflectedIfMatches(FlowState state)
    {
        ReflectedFlowKey reflectedKey = ToReflectedKey(state.Key);
        PeerFlowKey peerKey = ToPeerKey(state.Key);
        _reflected.TryRemove(new KeyValuePair<ReflectedFlowKey, FlowState>(reflectedKey, state));
        if (_reflectedByPeer.TryGetValue(peerKey, out ConcurrentDictionary<ReflectedFlowKey, FlowState>? bucket))
        {
            bucket.TryRemove(new KeyValuePair<ReflectedFlowKey, FlowState>(reflectedKey, state));
            RemovePeerBucketIfEmpty(peerKey, bucket);
        }
    }

    private void RemovePeerBucketIfEmpty(
        PeerFlowKey peerKey,
        ConcurrentDictionary<ReflectedFlowKey, FlowState> bucket)
    {
        if (bucket.IsEmpty)
        {
            _reflectedByPeer.TryRemove(
                new KeyValuePair<PeerFlowKey, ConcurrentDictionary<ReflectedFlowKey, FlowState>>(peerKey, bucket));
        }
    }

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

            if (_flows.TryRemove(new KeyValuePair<FlowKey, FlowState>(key, state)))
            {
                RemoveReflectedIfMatches(state);
            }
        }
    }
}
