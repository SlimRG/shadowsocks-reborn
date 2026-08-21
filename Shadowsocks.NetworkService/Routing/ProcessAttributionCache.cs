#nullable enable
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Shadowsocks.NetworkService.Routing;

internal static class ProcessAttributionCache
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<LocalEndpointKey, ConcurrentDictionary<int, DateTime>> Entries = new();
    private static long _lastSweepTicks;

    public static void Observe(byte protocol, ushort observedPort, int processId, DateTime nowUtc)
    {
        if (protocol is not 6 and not 17 || observedPort == 0 || processId <= 0)
            return;

        ObserveNormalized(protocol, observedPort, processId, nowUtc);
        ushort reversed = BinaryPrimitives.ReverseEndianness(observedPort);
        if (reversed != observedPort)
        {
            // WinDivert exposes the flow 5-tuple, but keeping both byte-order forms here
            // makes the cache robust across native representation details. Ambiguous ports
            // are deliberately rejected by TryGetProcessId rather than guessed.
            ObserveNormalized(protocol, reversed, processId, nowUtc);
        }
        MaybeSweep(nowUtc);
    }

    public static void Remove(byte protocol, ushort observedPort, int processId)
    {
        RemoveNormalized(protocol, observedPort, processId);
        ushort reversed = BinaryPrimitives.ReverseEndianness(observedPort);
        if (reversed != observedPort)
            RemoveNormalized(protocol, reversed, processId);
    }

    public static bool TryGetProcessId(byte protocol, ushort localPort, DateTime nowUtc, out int processId)
    {
        MaybeSweep(nowUtc);
        LocalEndpointKey key = new(protocol, localPort);
        if (!Entries.TryGetValue(key, out ConcurrentDictionary<int, DateTime>? candidates))
        {
            processId = 0;
            return false;
        }

        int resolved = 0;
        foreach ((int candidate, DateTime lastSeenUtc) in candidates)
        {
            if (nowUtc - lastSeenUtc > EntryLifetime)
                continue;
            if (resolved != 0 && resolved != candidate)
            {
                processId = 0;
                return false;
            }
            resolved = candidate;
        }

        processId = resolved;
        return processId > 0;
    }

    internal static void ClearForTests()
    {
        Entries.Clear();
        Interlocked.Exchange(ref _lastSweepTicks, 0);
    }

    private static void ObserveNormalized(byte protocol, ushort localPort, int processId, DateTime nowUtc)
    {
        LocalEndpointKey key = new(protocol, localPort);
        ConcurrentDictionary<int, DateTime> candidates = Entries.GetOrAdd(
            key,
            static _ => new ConcurrentDictionary<int, DateTime>());
        candidates[processId] = nowUtc;
    }

    private static void RemoveNormalized(byte protocol, ushort localPort, int processId)
    {
        LocalEndpointKey key = new(protocol, localPort);
        if (!Entries.TryGetValue(key, out ConcurrentDictionary<int, DateTime>? candidates))
            return;

        candidates.TryRemove(processId, out _);
        if (candidates.IsEmpty)
        {
            Entries.TryRemove(
                new KeyValuePair<LocalEndpointKey, ConcurrentDictionary<int, DateTime>>(key, candidates));
        }
    }

    private static void MaybeSweep(DateTime nowUtc)
    {
        long ticks = nowUtc.Ticks;
        long previous = Interlocked.Read(ref _lastSweepTicks);
        if (ticks - previous < TimeSpan.FromMinutes(1).Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, ticks, previous) != previous)
        {
            return;
        }

        foreach ((LocalEndpointKey key, ConcurrentDictionary<int, DateTime> candidates) in Entries)
        {
            foreach ((int processId, DateTime lastSeenUtc) in candidates)
            {
                if (nowUtc - lastSeenUtc > EntryLifetime)
                    candidates.TryRemove(new KeyValuePair<int, DateTime>(processId, lastSeenUtc));
            }
            if (candidates.IsEmpty)
            {
                Entries.TryRemove(
                    new KeyValuePair<LocalEndpointKey, ConcurrentDictionary<int, DateTime>>(key, candidates));
            }
        }
    }

    private readonly record struct LocalEndpointKey(byte Protocol, ushort LocalPort);
}
