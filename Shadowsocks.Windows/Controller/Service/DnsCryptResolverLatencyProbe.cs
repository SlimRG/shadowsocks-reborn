using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.Service
{
    internal static class DnsCryptResolverLatencyProbe
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1400);
        private const int DefaultConcurrency = 24;

        public static async Task<IReadOnlyDictionary<string, int>> ProbeAsync(
            IReadOnlyList<DnsCryptResolverInfo> resolvers,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resolvers);
            using var gate = new SemaphoreSlim(DefaultConcurrency, DefaultConcurrency);
            var results = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Task[] tasks = resolvers
                .Where(resolver => resolver is not null && !string.IsNullOrWhiteSpace(resolver.Name))
                .GroupBy(resolver => resolver.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(resolver => ProbeOneAsync(resolver, gate, results, cancellationToken))
                .ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static async Task ProbeOneAsync(
            DnsCryptResolverInfo resolver,
            SemaphoreSlim gate,
            ConcurrentDictionary<string, int> results,
            CancellationToken cancellationToken)
        {
            if (!TryGetEndpoint(resolver, out IPEndPoint endpoint))
                return;

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DefaultTimeout);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                    int elapsed = Math.Max(1, checked((int)Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    results[resolver.Name] = elapsed;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
            }
            finally
            {
                gate.Release();
            }
        }

        internal static bool TryGetEndpoint(DnsCryptResolverInfo resolver, out IPEndPoint endpoint)
        {
            endpoint = null;
            if (resolver is null)
                return false;

            IPAddress address = null;
            foreach (string candidate in resolver.Addresses ?? Array.Empty<string>())
            {
                string host = IpCountryService.NormalizeHost(candidate);
                if (IPAddress.TryParse(host, out IPAddress parsed) && IpCountryService.IsPublicAddress(parsed))
                {
                    address = parsed;
                    break;
                }
            }
            if (address is null)
                return false;

            int port = resolver.Ports?.FirstOrDefault(value => value is >= 1 and <= 65535) ?? 0;
            if (port == 0)
                port = 443;
            endpoint = new IPEndPoint(address, port);
            return true;
        }
    }
}
