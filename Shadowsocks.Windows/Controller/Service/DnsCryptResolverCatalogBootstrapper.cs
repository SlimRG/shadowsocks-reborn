#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace Shadowsocks.Controller.Service
{
    internal interface IDnsCryptResolverCatalogBootstrapper
    {
        Task EnsureFreshAsync(
            string cacheDirectory,
            string shadowsocksSocks5Host,
            int shadowsocksSocks5Port,
            CancellationToken cancellationToken);
    }

    internal sealed class DnsCryptResolverCatalogBootstrapper : IDnsCryptResolverCatalogBootstrapper
    {
        internal const string ResolverListFileName = "public-resolvers.md";
        internal const string ResolverListSignatureFileName = ResolverListFileName + ".minisig";

        private const int MaxCatalogBytes = 8 * 1024 * 1024;
        private const int MaxSignatureBytes = 64 * 1024;
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly TimeSpan Freshness = TimeSpan.FromHours(24);
        private static readonly Uri[] CatalogUris =
        [
            new("https://raw.githubusercontent.com/DNSCrypt/dnscrypt-resolvers/master/v3/public-resolvers.md"),
            new("https://download.dnscrypt.info/resolvers-list/v3/public-resolvers.md"),
            new("https://cdn.jsdelivr.net/gh/DNSCrypt/dnscrypt-resolvers@master/v3/public-resolvers.md"),
        ];

        private readonly Func<Uri, CancellationToken, Task<byte[]>>? testDownloader;
        private readonly Func<string, string, string, bool> signatureVerifier;

        public DnsCryptResolverCatalogBootstrapper()
            : this(null, MinisignVerifier.VerifyFileAllowLegacy)
        {
        }

        internal DnsCryptResolverCatalogBootstrapper(
            Func<Uri, CancellationToken, Task<byte[]>>? testDownloader,
            Func<string, string, string, bool>? signatureVerifier = null)
        {
            this.testDownloader = testDownloader;
            this.signatureVerifier = signatureVerifier ?? MinisignVerifier.VerifyFileAllowLegacy;
        }

        public async Task EnsureFreshAsync(
            string cacheDirectory,
            string shadowsocksSocks5Host,
            int shadowsocksSocks5Port,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(shadowsocksSocks5Host);
            ArgumentOutOfRangeException.ThrowIfLessThan(shadowsocksSocks5Port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(shadowsocksSocks5Port, IPEndPoint.MaxPort);

            string directory = Path.GetFullPath(cacheDirectory);
            Directory.CreateDirectory(directory);
            string catalogPath = Path.Combine(directory, ResolverListFileName);
            string signaturePath = Path.Combine(directory, ResolverListSignatureFileName);

            bool existingValid = IsValidCatalogPair(catalogPath, signaturePath);
            if (existingValid && DateTime.UtcNow - File.GetLastWriteTimeUtc(catalogPath) < Freshness)
                return;

            var failures = new List<Exception>();
            try
            {
                if (testDownloader is not null)
                {
                    await DownloadAndPublishAsync(
                        testDownloader, catalogPath, signaturePath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var resolver = new ShadowsocksDohResolver(shadowsocksSocks5Host, shadowsocksSocks5Port);
                    using HttpClient client = ShadowsocksDohHttpClient.CreateCatalogClient(
                        shadowsocksSocks5Host,
                        shadowsocksSocks5Port,
                        resolver);

                    async Task<byte[]> DownloadAsync(Uri uri, CancellationToken token)
                        => await DownloadBoundedAsync(
                            client,
                            uri,
                            uri.AbsolutePath.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase)
                                ? MaxSignatureBytes
                                : MaxCatalogBytes,
                            token).ConfigureAwait(false);

                    await DownloadAndPublishAsync(
                        DownloadAsync, catalogPath, signaturePath, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            // A stale cache is still cryptographically authenticated and is preferable to
            // plaintext/system-DNS fallback. Keep it available and retry refresh later.
            if (existingValid)
            {
                Logger.Warn(failures[0], "DNSCrypt signed resolver catalog refresh failed; using the existing verified cache.");
                return;
            }

            throw new DnsCryptBootstrapException(
                "DNSCrypt could not refresh the signed resolver catalog through Cloudflare/Google DoH over Shadowsocks.",
                failures[0]);
        }

        private async Task DownloadAndPublishAsync(
            Func<Uri, CancellationToken, Task<byte[]>> downloader,
            string catalogPath,
            string signaturePath,
            CancellationToken cancellationToken)
        {
            Exception? lastFailure = null;
            foreach (Uri catalogUri in CatalogUris)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string tempCatalog = catalogPath + $".{Guid.NewGuid():N}.new";
                string tempSignature = signaturePath + $".{Guid.NewGuid():N}.new";
                try
                {
                    byte[] catalog = await downloader(catalogUri, cancellationToken).ConfigureAwait(false);
                    byte[] signature = await downloader(new Uri(catalogUri.AbsoluteUri + ".minisig"), cancellationToken).ConfigureAwait(false);
                    if (catalog.Length is 0 or > MaxCatalogBytes)
                        throw new InvalidDataException("DNSCrypt resolver catalog has an invalid size.");
                    if (signature.Length is 0 or > MaxSignatureBytes)
                        throw new InvalidDataException("DNSCrypt resolver catalog signature has an invalid size.");

                    await File.WriteAllBytesAsync(tempCatalog, catalog, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(tempSignature, signature, cancellationToken).ConfigureAwait(false);
                    string signatureText = Encoding.UTF8.GetString(signature);
                    if (!signatureVerifier(tempCatalog, signatureText, DnsCryptTomlGenerator.PublicResolversMinisignKey))
                        throw new InvalidDataException("DNSCrypt resolver catalog Minisign verification failed.");

                    File.Move(tempCatalog, catalogPath, overwrite: true);
                    File.Move(tempSignature, signaturePath, overwrite: true);
                    DateTime now = DateTime.UtcNow;
                    File.SetLastWriteTimeUtc(catalogPath, now);
                    File.SetLastWriteTimeUtc(signaturePath, now);
                    Logger.Info("DNSCrypt signed resolver catalog refreshed through Shadowsocks: {0}", catalogUri);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                    Logger.Debug(exception, "DNSCrypt resolver catalog source failed through DoH-over-Shadowsocks: {0}", catalogUri);
                }
                finally
                {
                    TryDelete(tempCatalog);
                    TryDelete(tempSignature);
                }
            }

            throw new DnsCryptBootstrapException(
                "All DNSCrypt resolver catalog mirrors failed through DoH-over-Shadowsocks.",
                lastFailure);
        }

        private bool IsValidCatalogPair(string catalogPath, string signaturePath)
        {
            if (!File.Exists(catalogPath) || !File.Exists(signaturePath))
                return false;
            try
            {
                string signature = File.ReadAllText(signaturePath, Encoding.UTF8);
                return signatureVerifier(catalogPath, signature, DnsCryptTomlGenerator.PublicResolversMinisignKey);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                Logger.Debug(exception, "Existing DNSCrypt resolver catalog cache pair is invalid.");
                return false;
            }
        }

        private static async Task<byte[]> DownloadBoundedAsync(
            HttpClient client,
            Uri uri,
            int maxBytes,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > maxBytes)
                throw new InvalidDataException($"Response from {uri.Host} exceeds the allowed size.");
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadBoundedAsync(source, maxBytes, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<byte[]> ReadBoundedAsync(Stream source, int maxBytes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

            using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
            byte[] buffer = new byte[32 * 1024];
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (output.Length + read > maxBytes)
                    throw new InvalidDataException("Response exceeds the allowed size.");
                output.Write(buffer.AsSpan(0, read));
            }
            return output.ToArray();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }

    internal sealed class ShadowsocksDohResolver : IDisposable
    {
        private const ushort TypeA = 1;
        private const ushort TypeAaaa = 28;
        private const int MaxDnsMessageBytes = 64 * 1024;
        private static readonly ushort[] QueryTypes = [TypeA, TypeAaaa];
        private static readonly DohProvider[] Providers =
        [
            new("Cloudflare", new Uri("https://cloudflare-dns.com/dns-query")),
            new("Google", new Uri("https://dns.google/dns-query")),
        ];

        private readonly HttpClient client;
        private readonly Func<DohProvider, byte[], CancellationToken, Task<byte[]>>? queryOverride;

        internal ShadowsocksDohResolver(
            string proxyHost,
            int proxyPort,
            Func<DohProvider, byte[], CancellationToken, Task<byte[]>>? queryOverride = null)
        {
            this.queryOverride = queryOverride;
            client = ShadowsocksDohHttpClient.CreateProviderClient(proxyHost, proxyPort);
        }

        internal async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal))
                return [literal];

            Exception? lastFailure = null;
            foreach (ushort type in QueryTypes)
            {
                foreach (DohProvider provider in Providers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ushort transactionId = RandomTransactionId();
                    byte[] query = BuildQuery(host, type, transactionId);
                    try
                    {
                        byte[] response = queryOverride is null
                            ? await QueryProviderAsync(provider, query, cancellationToken).ConfigureAwait(false)
                            : await queryOverride(provider, query, cancellationToken).ConfigureAwait(false);
                        IPAddress[] addresses = ParseAddresses(response, transactionId, type);
                        if (addresses.Length > 0)
                            return addresses;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        lastFailure = exception;
                    }
                }
            }

            throw new DnsCryptBootstrapException(
                $"Cloudflare and Google DoH could not resolve '{host}' through Shadowsocks.",
                lastFailure);
        }

        private async Task<byte[]> QueryProviderAsync(
            DohProvider provider,
            byte[] query,
            CancellationToken cancellationToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, provider.Endpoint);
            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            request.Content = content;
            request.Headers.Accept.ParseAdd("application/dns-message");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await DnsCryptResolverCatalogBootstrapper.ReadBoundedAsync(
                stream,
                MaxDnsMessageBytes,
                cancellationToken).ConfigureAwait(false);
        }

        internal static byte[] BuildQuery(string host, ushort queryType, ushort transactionId)
        {
            string normalized = host.Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(normalized))
                throw new ArgumentException("A DNS hostname is required.", nameof(host));

            using var output = new MemoryStream(256);
            Span<byte> header = stackalloc byte[12];
            header.Clear();
            BinaryPrimitives.WriteUInt16BigEndian(header, transactionId);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
            BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
            output.Write(header);
            foreach (string label in normalized.Split('.'))
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length is 0 or > 63)
                    throw new ArgumentException("DNS hostname contains an invalid label.", nameof(host));
                output.WriteByte((byte)bytes.Length);
                output.Write(bytes);
            }
            output.WriteByte(0);
            Span<byte> tail = stackalloc byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(tail, queryType);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1);
            output.Write(tail);
            return output.ToArray();
        }

        internal static IPAddress[] ParseAddresses(byte[] response, ushort expectedTransactionId, ushort queryType)
        {
            ArgumentNullException.ThrowIfNull(response);
            ReadOnlySpan<byte> data = response;
            if (data.Length < 12)
                throw new InvalidDataException("DoH returned a truncated DNS response.");
            if (BinaryPrimitives.ReadUInt16BigEndian(data) != expectedTransactionId)
                throw new InvalidDataException("DoH returned a DNS response with a mismatched transaction ID.");
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
                throw new InvalidDataException("DoH returned an unsuccessful DNS response.");

            int questionCount = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            int answerCount = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
            int offset = 12;
            for (int i = 0; i < questionCount; i++)
            {
                offset = SkipName(data, offset);
                EnsureAvailable(data, offset, 4);
                offset += 4;
            }

            var addresses = new List<IPAddress>();
            for (int i = 0; i < answerCount; i++)
            {
                offset = SkipName(data, offset);
                EnsureAvailable(data, offset, 10);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                ushort dnsClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
                offset += 10;
                EnsureAvailable(data, offset, dataLength);
                if (dnsClass == 1 && type == queryType
                    && ((type == TypeA && dataLength == 4) || (type == TypeAaaa && dataLength == 16)))
                {
                    addresses.Add(new IPAddress(data.Slice(offset, dataLength)));
                }
                offset += dataLength;
            }
            return addresses.Distinct().ToArray();
        }

        private static int SkipName(ReadOnlySpan<byte> data, int offset)
        {
            while (true)
            {
                EnsureAvailable(data, offset, 1);
                byte length = data[offset++];
                if (length == 0)
                    return offset;
                if ((length & 0xC0) == 0xC0)
                {
                    EnsureAvailable(data, offset, 1);
                    return offset + 1;
                }
                if ((length & 0xC0) != 0 || length > 63)
                    throw new InvalidDataException("DoH returned an invalid DNS name.");
                EnsureAvailable(data, offset, length);
                offset += length;
            }
        }

        private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int count)
        {
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new InvalidDataException("DoH returned a truncated DNS response.");
        }

        private static ushort RandomTransactionId()
        {
            Span<byte> bytes = stackalloc byte[2];
            RandomNumberGenerator.Fill(bytes);
            return BinaryPrimitives.ReadUInt16BigEndian(bytes);
        }

        public void Dispose() => client.Dispose();

        internal sealed record DohProvider(string Name, Uri Endpoint);
    }

    internal static class ShadowsocksDohHttpClient
    {
        internal static HttpClient CreateProviderClient(string proxyHost, int proxyPort)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    string host = context.DnsEndPoint.Host;
                    string destinationIp = string.Equals(host, "cloudflare-dns.com", StringComparison.OrdinalIgnoreCase)
                        ? "1.1.1.1"
                        : string.Equals(host, "dns.google", StringComparison.OrdinalIgnoreCase)
                            ? "8.8.8.8"
                            : throw new InvalidOperationException($"Unexpected DoH provider host '{host}'.");
                    Socket socket = await Socks5Connector.ConnectAsync(
                        proxyHost,
                        proxyPort,
                        destinationIp,
                        context.DnsEndPoint.Port,
                        cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
        }

        internal static HttpClient CreateCatalogClient(
            string proxyHost,
            int proxyPort,
            ShadowsocksDohResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            return CreateTunneledClient(
                () => proxyHost,
                () => proxyPort,
                TimeSpan.FromSeconds(30),
                allowAutoRedirect: false,
                (host, _, cancellationToken) => resolver.ResolveAsync(host, cancellationToken));
        }

        internal static HttpClient CreateTunneledClient(
            Func<string> proxyHostProvider,
            Func<int> proxyPortProvider,
            TimeSpan timeout,
            bool allowAutoRedirect = true)
        {
            ArgumentNullException.ThrowIfNull(proxyHostProvider);
            ArgumentNullException.ThrowIfNull(proxyPortProvider);
            return CreateTunneledClient(
                proxyHostProvider,
                proxyPortProvider,
                timeout,
                allowAutoRedirect,
                static async (host, endpoint, cancellationToken) =>
                {
                    using var resolver = new ShadowsocksDohResolver(endpoint.ProxyHost, endpoint.ProxyPort);
                    return await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
                });
        }

        private static HttpClient CreateTunneledClient(
            Func<string> proxyHostProvider,
            Func<int> proxyPortProvider,
            TimeSpan timeout,
            bool allowAutoRedirect,
            Func<string, ProxyEndpoint, CancellationToken, Task<IPAddress[]>> resolver)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = allowAutoRedirect,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    string proxyHost = proxyHostProvider();
                    int proxyPort = proxyPortProvider();
                    ArgumentException.ThrowIfNullOrWhiteSpace(proxyHost);
                    if (proxyPort is < 1 or > IPEndPoint.MaxPort)
                        throw new DnsCryptComponentNetworkException(
                            $"The Shadowsocks SOCKS5 port '{proxyPort}' is not valid for DNSCrypt upstream access.");

                    var endpoint = new ProxyEndpoint(proxyHost, proxyPort);
                    IPAddress[] addresses = await resolver(
                        context.DnsEndPoint.Host,
                        endpoint,
                        cancellationToken).ConfigureAwait(false);
                    Exception? lastFailure = null;
                    foreach (IPAddress address in addresses)
                    {
                        try
                        {
                            Socket socket = await Socks5Connector.ConnectAsync(
                                proxyHost,
                                proxyPort,
                                address.ToString(),
                                context.DnsEndPoint.Port,
                                cancellationToken).ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            lastFailure = exception;
                        }
                    }

                    throw new HttpRequestException(
                        $"No DoH-resolved address for '{context.DnsEndPoint.Host}' was reachable through Shadowsocks.",
                        lastFailure);
                },
            };
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = timeout,
            };
        }

        private readonly record struct ProxyEndpoint(string ProxyHost, int ProxyPort);
    }

    internal sealed class NoOpDnsCryptResolverCatalogBootstrapper : IDnsCryptResolverCatalogBootstrapper
    {
        internal static NoOpDnsCryptResolverCatalogBootstrapper Instance { get; } = new();

        private NoOpDnsCryptResolverCatalogBootstrapper()
        {
        }

        public Task EnsureFreshAsync(
            string cacheDirectory,
            string shadowsocksSocks5Host,
            int shadowsocksSocks5Port,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
