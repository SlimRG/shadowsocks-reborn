using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shadowsocks.Controller.Service
{
    public sealed partial class DnsCryptRuntimeManager
    {
        internal static Version ParseReportedVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;
            foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string value = line.Trim();
                if (value.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                    value = value.Substring(1);
                if (Version.TryParse(value, out Version parsed))
                    return parsed;
            }
            return null;
        }

        internal static int AllocateLoopbackPort()
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                TcpListener tcp = null;
                UdpClient udp = null;
                try
                {
                    tcp = new TcpListener(IPAddress.Loopback, 0);
                    tcp.Start();
                    int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                    udp = new UdpClient(AddressFamily.InterNetwork);
                    udp.Client.ExclusiveAddressUse = true;
                    udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    return port;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    udp?.Dispose();
                    try { tcp?.Stop(); } catch { }
                }
            }
            throw new InvalidOperationException("Unable to allocate a loopback port for DNSCrypt Proxy.");
        }

        internal static async Task<bool> DnsHealthCheckAsync(
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (port is < 1 or > 65535)
                return false;
            ushort transactionId = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
            byte[] query = BuildDnsAQuery(transactionId, "example.com");
            using var client = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                client.Connect(new IPEndPoint(IPAddress.Loopback, port));
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                await client.SendAsync(query, query.Length).ConfigureAwait(false);
                UdpReceiveResult response = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                return IsValidDnsResponse(response.Buffer, transactionId, "example.com");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(
            string hostName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("A DNS host name is required.", nameof(hostName));
            if (IPAddress.TryParse(hostName.Trim(), out IPAddress parsed))
                return [parsed];

            DnsCryptRuntimeStatus current = GetStatus();
            if (!current.IsServing || current.Port is < 1 or > 65535)
                throw new InvalidOperationException("DNSCrypt Proxy is not serving DNS requests.");

            bool queryIpv4;
            bool queryIpv6;
            lock (stateLock)
            {
                queryIpv4 = lastStartOptions?.Config?.ipv4Servers != false;
                queryIpv6 = lastStartOptions?.Config?.ipv6Servers == true;
            }

            var addresses = new List<IPAddress>();
            if (queryIpv4)
                addresses.AddRange(await QueryAddressesAsync(current.Port, hostName.Trim(), 1, cancellationToken).ConfigureAwait(false));
            if (queryIpv6)
                addresses.AddRange(await QueryAddressesAsync(current.Port, hostName.Trim(), 28, cancellationToken).ConfigureAwait(false));

            IPAddress[] distinct = addresses.Distinct().ToArray();
            if (distinct.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);
            return distinct;
        }

        private static async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(
            int port,
            string hostName,
            ushort queryType,
            CancellationToken cancellationToken)
        {
            ushort transactionId = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
            byte[] query = BuildDnsQuery(transactionId, hostName, queryType);
            using var client = new UdpClient(AddressFamily.InterNetwork);
            client.Connect(new IPEndPoint(IPAddress.Loopback, port));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            await client.SendAsync(query, query.Length).ConfigureAwait(false);
            UdpReceiveResult response = await client.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            return ParseAddressResponse(response.Buffer, transactionId, queryType);
        }

        internal static IReadOnlyList<IPAddress> ParseAddressResponse(
            byte[] response,
            ushort transactionId,
            ushort queryType)
        {
            if (response is null || response.Length < 12)
                return [];
            if (((response[0] << 8) | response[1]) != transactionId)
                return [];
            ushort flags = (ushort)((response[2] << 8) | response[3]);
            if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0)
                return [];

            int questionCount = (response[4] << 8) | response[5];
            int answerCount = (response[6] << 8) | response[7];
            int offset = 12;
            for (int i = 0; i < questionCount; i++)
            {
                if (!SkipDnsName(response, ref offset) || offset + 4 > response.Length)
                    return [];
                offset += 4;
            }

            var addresses = new List<IPAddress>();
            for (int i = 0; i < answerCount; i++)
            {
                if (!SkipDnsName(response, ref offset) || offset + 10 > response.Length)
                    return addresses;
                ushort type = (ushort)((response[offset] << 8) | response[offset + 1]);
                ushort dnsClass = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                ushort dataLength = (ushort)((response[offset + 8] << 8) | response[offset + 9]);
                offset += 10;
                if (offset + dataLength > response.Length)
                    return addresses;

                if (dnsClass == 1 && type == queryType)
                {
                    if (type == 1 && dataLength == 4)
                        addresses.Add(new IPAddress(response.AsSpan(offset, 4)));
                    else if (type == 28 && dataLength == 16)
                        addresses.Add(new IPAddress(response.AsSpan(offset, 16)));
                }
                offset += dataLength;
            }
            return addresses;
        }

        private static bool SkipDnsName(byte[] message, ref int offset)
        {
            int labels = 0;
            while (offset < message.Length && labels++ < 128)
            {
                byte length = message[offset++];
                if (length == 0)
                    return true;
                if ((length & 0xC0) == 0xC0)
                {
                    if (offset >= message.Length)
                        return false;
                    offset++;
                    return true;
                }
                if ((length & 0xC0) != 0 || length > 63 || offset + length > message.Length)
                    return false;
                offset += length;
            }
            return false;
        }

        internal static byte[] BuildDnsAQuery(ushort transactionId, string hostName)
            => BuildDnsQuery(transactionId, hostName, 1);

        private static byte[] BuildDnsQuery(ushort transactionId, string hostName, ushort queryType)
        {
            if (string.IsNullOrWhiteSpace(hostName))
                throw new ArgumentException("A DNS host name is required.", nameof(hostName));
            if (queryType is not 1 and not 28)
                throw new ArgumentOutOfRangeException(nameof(queryType));
            string[] labels = hostName.TrimEnd('.').Split('.');
            using var stream = new MemoryStream(64);
            WriteUInt16NetworkOrder(stream, transactionId);
            WriteUInt16NetworkOrder(stream, 0x0100);
            WriteUInt16NetworkOrder(stream, 1);
            WriteUInt16NetworkOrder(stream, 0);
            WriteUInt16NetworkOrder(stream, 0);
            WriteUInt16NetworkOrder(stream, 0);
            foreach (string label in labels)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(label);
                if (bytes.Length is < 1 or > 63)
                    throw new ArgumentException("DNS host name contains an invalid label.", nameof(hostName));
                stream.WriteByte((byte)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
            stream.WriteByte(0);
            WriteUInt16NetworkOrder(stream, queryType);
            WriteUInt16NetworkOrder(stream, 1);
            return stream.ToArray();
        }

        internal static bool IsValidDnsResponse(byte[] response, ushort transactionId, string expectedHostName = "example.com")
        {
            if (response is null || response.Length < 17)
                return false;
            ushort responseId = (ushort)((response[0] << 8) | response[1]);
            if (responseId != transactionId)
                return false;
            ushort flags = (ushort)((response[2] << 8) | response[3]);
            bool isResponse = (flags & 0x8000) != 0;
            int responseCode = flags & 0x000F;
            ushort questionCount = (ushort)((response[4] << 8) | response[5]);
            if (!isResponse || questionCount < 1 || responseCode != 0)
                return false;

            if (string.IsNullOrWhiteSpace(expectedHostName))
                return false;

            int offset = 12;
            var questionName = new StringBuilder();
            while (true)
            {
                if (offset >= response.Length)
                    return false;
                int labelLength = response[offset++];
                if (labelLength == 0)
                    break;
                // A compressed QNAME in the echoed question is legal DNS, but DNSCrypt
                // health-check responses to our simple query should echo the literal name.
                // Reject compression here to keep validation deterministic and spoof-resistant.
                if ((labelLength & 0xC0) != 0 || labelLength > 63 || offset + labelLength > response.Length)
                    return false;
                if (questionName.Length > 0)
                    questionName.Append('.');
                for (int index = 0; index < labelLength; index++)
                {
                    byte value = response[offset + index];
                    if (value is < 0x21 or > 0x7E)
                        return false;
                    questionName.Append((char)value);
                }
                offset += labelLength;
            }

            if (!string.Equals(questionName.ToString(), expectedHostName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                return false;
            if (offset + 4 > response.Length)
                return false;
            ushort queryType = (ushort)((response[offset] << 8) | response[offset + 1]);
            ushort queryClass = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
            return queryType == 1 && queryClass == 1;
        }

        private static void WriteUInt16NetworkOrder(Stream stream, ushort value)
        {
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)(value & 0xFF));
        }

        internal static IReadOnlyDictionary<string, int> ParseResolverLatencies(string output)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in SplitLines(output))
            {
                int okEnd = line.IndexOf("] OK", StringComparison.OrdinalIgnoreCase);
                if (okEnd <= 0)
                    continue;
                int nameStart = line.LastIndexOf('[', okEnd);
                if (nameStart < 0 || nameStart + 1 >= okEnd)
                    continue;
                int rttMarker = line.IndexOf("rtt:", okEnd, StringComparison.OrdinalIgnoreCase);
                if (rttMarker < 0)
                    continue;
                int digitStart = rttMarker + 4;
                while (digitStart < line.Length && char.IsWhiteSpace(line[digitStart]))
                    digitStart++;
                int digitEnd = digitStart;
                while (digitEnd < line.Length && char.IsDigit(line[digitEnd]))
                    digitEnd++;
                if (digitEnd == digitStart || !int.TryParse(line[digitStart..digitEnd], out int latencyMs))
                    continue;
                string name = line[(nameStart + 1)..okEnd].Trim();
                if (name.Length == 0)
                    continue;
                if (!result.TryGetValue(name, out int current) || latencyMs < current)
                    result[name] = latencyMs;
            }
            return result;
        }

        internal static string ParseSelectedResolverName(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return string.Empty;

            const string marker = "Server with the lowest initial latency:";
            int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return string.Empty;

            int start = markerIndex + marker.Length;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
                start++;
            int end = line.IndexOf(' ', start);
            int comma = line.IndexOf(',', start);
            if (end < 0 || (comma >= 0 && comma < end))
                end = comma;
            if (end < 0)
                end = line.Length;
            return line[start..end].Trim();
        }

        private string BuildStartupTimeoutMessage()
        {
            string error;
            lock (stateLock)
                error = lastRuntimeUpstreamError;
            string baseMessage = $"DNSCrypt Proxy did not pass the DNS health-check within {startupTimeout.TotalSeconds:0.###} seconds.";
            return string.IsNullOrWhiteSpace(error)
                ? baseMessage
                : $"{baseMessage} Last dnscrypt-proxy error: {error}";
        }

        private void RecordRuntimeResolverMetrics(string line, string level)
        {
            bool notify = false;
            string[] configuredNames;
            lock (stateLock)
            {
                configuredNames = lastStartOptions?.Config?.serverNames?
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();
            }

            foreach ((string name, int latencyMs) in ParseResolverLatencies(line))
            {
                bool changed = !resolverLatencyCache.TryGetValue(name, out int previousLatency) || previousLatency != latencyMs;
                resolverLatencyCache[name] = latencyMs;
                if (changed && configuredNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    notify = true;
            }

            string selected = ParseSelectedResolverName(line);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                lock (stateLock)
                {
                    if (!string.Equals(selectedRuntimeResolverName, selected, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedRuntimeResolverName = selected;
                        notify = true;
                    }
                }
            }

            if (level is "FATAL" or "CRITICAL" or "ERROR")
            {
                string value = line?.Trim() ?? string.Empty;
                if (value.Length > 700)
                    value = value[..700];
                lock (stateLock)
                    lastRuntimeUpstreamError = value;
            }

            if (notify)
                ResolverMetricsChanged?.Invoke(this, EventArgs.Empty);
        }

        private bool IsDiagnosticLoggingEnabled()
        {
            try
            {
                return diagnosticLoggingEnabled?.Invoke() != false;
            }
            catch
            {
                return false;
            }
        }

        private bool IsVerboseDiagnosticLoggingEnabled()
        {
            if (!IsDiagnosticLoggingEnabled())
                return false;

            try
            {
                return verboseLoggingEnabled?.Invoke() != false;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsResolverProbeDiagnostic(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            string[] markers = ["[NOTICE]", "[INFO]", "[WARNING]", "[WARN]", "[DEBUG]"];
            foreach (string marker in markers)
            {
                int markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                    continue;

                string payload = line[(markerIndex + marker.Length)..].TrimStart();
                if (payload.StartsWith("[", StringComparison.Ordinal))
                    return true;
            }

            return line.Contains("rtt:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("additional certificate", StringComparison.OrdinalIgnoreCase)
                || line.Contains("post-quantum", StringComparison.OrdinalIgnoreCase)
                || line.Contains(" TIMEOUT", StringComparison.OrdinalIgnoreCase);
        }

        private void LogCommandOutput(string phase, DnsCryptCommandResult result)
        {
            if (!IsDiagnosticLoggingEnabled())
                return;

            bool verboseDiagnostics = IsVerboseDiagnosticLoggingEnabled();
            if (verboseDiagnostics)
            {
                foreach (string line in SplitLines(result.StandardOutput))
                    Logger.Info($"DNSCryptProxy | {phase} | {line}");
            }

            foreach (string line in SplitLines(result.StandardError))
            {
                string level = DetectRuntimeLogLevel(line, error: true);
                string message = $"DNSCryptProxy | {phase} | {line}";
                if (level is "FATAL" or "CRITICAL" or "ERROR")
                    Logger.Error(message);
                else if (verboseDiagnostics)
                    Logger.Warn(message);
            }
        }

        private void LogSettingsValidationLine(bool error, string line, Action<string> captureError)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            string level = DetectRuntimeLogLevel(line, error);
            foreach ((string name, int latencyMs) in ParseResolverLatencies(line))
                resolverLatencyCache[name] = latencyMs;
            if (level is "FATAL" or "CRITICAL" or "ERROR")
            {
                string value = line.Trim();
                if (value.Length > 700)
                    value = value[..700];
                captureError?.Invoke(value);
            }

            string message = $"DNSCryptProxy | SETTINGS-VALIDATE | {line}";
            bool resolverProbe = IsResolverProbeDiagnostic(line);
            bool dnsLogging = IsDiagnosticLoggingEnabled();
            if (!dnsLogging)
                return;
            bool verboseDiagnostics = resolverProbe && IsVerboseDiagnosticLoggingEnabled();
            if (level is "FATAL" or "CRITICAL" or "ERROR")
                Logger.Error(message);
            else if (level is "WARN" or "STDERR")
            {
                if (resolverProbe ? verboseDiagnostics : dnsLogging)
                    Logger.Warn(message);
            }
            else if (level == "DEBUG")
            {
                if (IsVerboseDiagnosticLoggingEnabled())
                    Logger.Debug(message);
            }
            else if (resolverProbe ? verboseDiagnostics : dnsLogging)
                Logger.Info(message);
        }

        private void LogRuntimeLine(bool error, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            string level = DetectRuntimeLogLevel(line, error);
            RecordRuntimeResolverMetrics(line, level);
            bool dnsLogging = IsDiagnosticLoggingEnabled();
            if (!dnsLogging)
                return;

            string message = $"DNSCryptProxy | {level} | {line}";
            bool resolverProbe = IsResolverProbeDiagnostic(line);
            bool verboseDiagnostics = resolverProbe && IsVerboseDiagnosticLoggingEnabled();
            switch (level)
            {
                case "FATAL":
                    Logger.Fatal(message);
                    break;
                case "CRITICAL":
                case "ERROR":
                    Logger.Error(message);
                    break;
                case "WARN":
                case "STDERR":
                    if (resolverProbe ? verboseDiagnostics : dnsLogging)
                        Logger.Warn(message);
                    break;
                case "DEBUG":
                    if (IsVerboseDiagnosticLoggingEnabled())
                        Logger.Debug(message);
                    break;
                default:
                    if (resolverProbe ? verboseDiagnostics : dnsLogging)
                        Logger.Info(message);
                    break;
            }
        }

        private static string DetectRuntimeLogLevel(string line, bool error)
        {
            if (line.Contains("[FATAL]", StringComparison.OrdinalIgnoreCase)) return "FATAL";
            if (line.Contains("[CRITICAL]", StringComparison.OrdinalIgnoreCase)) return "CRITICAL";
            if (line.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase)) return "ERROR";
            if (line.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase)
                || line.Contains("[WARN]", StringComparison.OrdinalIgnoreCase)) return "WARN";
            if (line.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)) return "DEBUG";
            if (line.Contains("[NOTICE]", StringComparison.OrdinalIgnoreCase)) return "NOTICE";
            if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase)) return "INFO";
            return error ? "STDERR" : "INFO";
        }

        private static IEnumerable<string> SplitLines(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim());

    }
}
