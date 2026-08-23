using System.Buffers.Binary;
using System.Text;

namespace Shadowsocks.NetworkService.Routing;

internal enum TcpDestinationInspectionStatus
{
    NeedMoreData = 0,
    Identified = 1,
    Unknown = 2,
}

internal readonly record struct TcpDestinationIdentity(string Host, string Url, string Protocol);

/// <summary>
/// Extracts only clear-text routing metadata from the beginning of an intercepted TCP stream.
/// HTTP Host and TLS ClientHello SNI are sufficient for domain policy without decrypting TLS,
/// installing a CA certificate, or modifying application payloads.
/// </summary>
internal static class InitialTcpProtocolInspector
{
    private const int MaxHttpHeaderBytes = 64 * 1024;
    private const int MaxTlsHelloBytes = 64 * 1024;

    private static readonly string[] HttpMethods =
    [
        "GET ", "POST ", "PUT ", "DELETE ", "HEAD ", "OPTIONS ", "PATCH ",
        "TRACE ", "CONNECT ",
    ];

    public static TcpDestinationInspectionStatus Inspect(
        ReadOnlySpan<byte> bytes,
        ushort remotePort,
        out TcpDestinationIdentity identity)
    {
        identity = default;
        if (bytes.IsEmpty)
        {
            return TcpDestinationInspectionStatus.NeedMoreData;
        }

        if (LooksLikeHttpPrefix(bytes))
        {
            return InspectHttp(bytes, remotePort, out identity);
        }

        if (bytes[0] == 0x16)
        {
            return InspectTls(bytes, remotePort, out identity);
        }

        return TcpDestinationInspectionStatus.Unknown;
    }

    private static TcpDestinationInspectionStatus InspectHttp(
        ReadOnlySpan<byte> bytes,
        ushort remotePort,
        out TcpDestinationIdentity identity)
    {
        identity = default;
        int headerEnd = bytes.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return bytes.Length >= MaxHttpHeaderBytes
                ? TcpDestinationInspectionStatus.Unknown
                : TcpDestinationInspectionStatus.NeedMoreData;
        }

        string headers = Encoding.Latin1.GetString(bytes[..(headerEnd + 4)]);
        int firstLineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        if (firstLineEnd <= 0)
        {
            return TcpDestinationInspectionStatus.Unknown;
        }

        string[] requestParts = headers[..firstLineEnd]
            .Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length != 3)
        {
            return TcpDestinationInspectionStatus.Unknown;
        }

        string method = requestParts[0];
        string target = requestParts[1];
        if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryNormalizeAuthority(target, remotePort == 0 ? 443 : remotePort, 443, out string host, out string authority))
            {
                return TcpDestinationInspectionStatus.Unknown;
            }

            identity = new TcpDestinationIdentity(host, "https://" + authority + "/", "http-connect");
            return TcpDestinationInspectionStatus.Identified;
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute)
            && (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            identity = new TcpDestinationIdentity(absolute.Host.TrimEnd('.'), absolute.AbsoluteUri, "http");
            return TcpDestinationInspectionStatus.Identified;
        }

        string? hostHeader = null;
        int cursor = firstLineEnd + 2;
        while (cursor < headerEnd)
        {
            int next = headers.IndexOf("\r\n", cursor, StringComparison.Ordinal);
            if (next < 0 || next > headerEnd)
            {
                break;
            }

            ReadOnlySpan<char> line = headers.AsSpan(cursor, next - cursor);
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().ToString().Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostHeader = line[(colon + 1)..].Trim().ToString();
                break;
            }
            cursor = next + 2;
        }

        if (string.IsNullOrWhiteSpace(hostHeader)
            || !TryNormalizeAuthority(hostHeader, remotePort == 0 ? 80 : remotePort, 80, out string requestHost, out string requestAuthority))
        {
            return TcpDestinationInspectionStatus.Unknown;
        }

        string path = target.StartsWith('/') ? target : "/";
        identity = new TcpDestinationIdentity(requestHost, "http://" + requestAuthority + path, "http");
        return TcpDestinationInspectionStatus.Identified;
    }

    private static TcpDestinationInspectionStatus InspectTls(
        ReadOnlySpan<byte> bytes,
        ushort remotePort,
        out TcpDestinationIdentity identity)
    {
        identity = default;
        if (bytes.Length > MaxTlsHelloBytes)
        {
            return TcpDestinationInspectionStatus.Unknown;
        }

        List<byte> handshake = [];
        int recordOffset = 0;
        while (true)
        {
            if (bytes.Length - recordOffset < 5)
            {
                return TcpDestinationInspectionStatus.NeedMoreData;
            }

            byte contentType = bytes[recordOffset];
            if (contentType != 0x16)
            {
                return TcpDestinationInspectionStatus.Unknown;
            }

            int recordLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(recordOffset + 3, 2));
            int payloadOffset = recordOffset + 5;
            if (recordLength <= 0 || bytes.Length - payloadOffset < recordLength)
            {
                return TcpDestinationInspectionStatus.NeedMoreData;
            }

            handshake.AddRange(bytes.Slice(payloadOffset, recordLength).ToArray());
            if (handshake.Count >= 4)
            {
                if (handshake[0] != 0x01)
                {
                    return TcpDestinationInspectionStatus.Unknown;
                }

                int helloLength = (handshake[1] << 16) | (handshake[2] << 8) | handshake[3];
                if (helloLength <= 0 || helloLength > MaxTlsHelloBytes)
                {
                    return TcpDestinationInspectionStatus.Unknown;
                }

                if (handshake.Count >= helloLength + 4)
                {
                    ReadOnlySpan<byte> hello = handshake.ToArray().AsSpan(4, helloLength);
                    if (!TryReadServerName(hello, out string host))
                    {
                        return TcpDestinationInspectionStatus.Unknown;
                    }

                    string authority = remotePort is 0 or 443
                        ? host
                        : host + ":" + remotePort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    identity = new TcpDestinationIdentity(host, "https://" + authority + "/", "tls-sni");
                    return TcpDestinationInspectionStatus.Identified;
                }
            }

            recordOffset = payloadOffset + recordLength;
            if (recordOffset >= bytes.Length)
            {
                return TcpDestinationInspectionStatus.NeedMoreData;
            }
        }
    }

    private static bool TryReadServerName(ReadOnlySpan<byte> hello, out string host)
    {
        host = string.Empty;
        int offset = 0;
        if (!Skip(hello, ref offset, 2 + 32)) // legacy_version + random
        {
            return false;
        }

        if (!TryReadByteVector(hello, ref offset, out _)
            || !TryReadUInt16Vector(hello, ref offset, out _)
            || !TryReadByteVector(hello, ref offset, out _))
        {
            return false;
        }

        if (offset == hello.Length)
        {
            return false; // ClientHello without extensions/SNI.
        }

        if (!TryReadUInt16Vector(hello, ref offset, out ReadOnlySpan<byte> extensions))
        {
            return false;
        }

        int extensionOffset = 0;
        while (extensionOffset + 4 <= extensions.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(extensions.Slice(extensionOffset, 2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(extensions.Slice(extensionOffset + 2, 2));
            extensionOffset += 4;
            if (length < 0 || extensionOffset + length > extensions.Length)
            {
                return false;
            }

            if (type == 0x0000)
            {
                ReadOnlySpan<byte> serverNames = extensions.Slice(extensionOffset, length);
                if (serverNames.Length < 2)
                {
                    return false;
                }

                int listLength = BinaryPrimitives.ReadUInt16BigEndian(serverNames[..2]);
                if (listLength > serverNames.Length - 2)
                {
                    return false;
                }

                int nameOffset = 2;
                int listEnd = 2 + listLength;
                while (nameOffset + 3 <= listEnd)
                {
                    byte nameType = serverNames[nameOffset];
                    int nameLength = BinaryPrimitives.ReadUInt16BigEndian(serverNames.Slice(nameOffset + 1, 2));
                    nameOffset += 3;
                    if (nameLength <= 0 || nameOffset + nameLength > listEnd)
                    {
                        return false;
                    }

                    if (nameType == 0)
                    {
                        string candidate = Encoding.ASCII.GetString(serverNames.Slice(nameOffset, nameLength)).Trim().TrimEnd('.');
                        if (IsUsableHost(candidate))
                        {
                            host = candidate;
                            return true;
                        }
                    }
                    nameOffset += nameLength;
                }
            }

            extensionOffset += length;
        }

        return false;
    }

    private static bool TryReadByteVector(ReadOnlySpan<byte> source, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (offset >= source.Length)
        {
            return false;
        }

        int length = source[offset++];
        if (offset + length > source.Length)
        {
            return false;
        }

        value = source.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadUInt16Vector(ReadOnlySpan<byte> source, ref int offset, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (offset + 2 > source.Length)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(offset, 2));
        offset += 2;
        if (offset + length > source.Length)
        {
            return false;
        }

        value = source.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool Skip(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        if (length < 0 || offset + length > source.Length)
        {
            return false;
        }
        offset += length;
        return true;
    }

    private static bool LooksLikeHttpPrefix(ReadOnlySpan<byte> bytes)
    {
        foreach (string method in HttpMethods)
        {
            ReadOnlySpan<byte> ascii = Encoding.ASCII.GetBytes(method);
            int comparable = Math.Min(bytes.Length, ascii.Length);
            if (bytes[..comparable].SequenceEqual(ascii[..comparable]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryNormalizeAuthority(
        string value,
        int transportPort,
        int schemeDefaultPort,
        out string host,
        out string authority)
    {
        host = string.Empty;
        authority = string.Empty;
        string candidate = value.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (!Uri.TryCreate("http://" + candidate, UriKind.Absolute, out Uri? uri)
            || !IsUsableHost(uri.Host))
        {
            return false;
        }

        host = uri.Host.TrimEnd('.');
        bool explicitPort = HasExplicitPort(candidate);
        int port = explicitPort ? uri.Port : transportPort;
        bool defaultForScheme = port == schemeDefaultPort;
        if (host.Contains(':'))
        {
            authority = defaultForScheme ? "[" + host + "]" : "[" + host + "]:" + port;
        }
        else
        {
            authority = defaultForScheme ? host : host + ":" + port;
        }
        return true;
    }


    private static bool HasExplicitPort(string authority)
    {
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            return close >= 0 && close + 1 < authority.Length && authority[close + 1] == ':';
        }

        int firstColon = authority.IndexOf(':');
        return firstColon >= 0 && firstColon == authority.LastIndexOf(':');
    }

    private static bool IsUsableHost(string host)
        => !string.IsNullOrWhiteSpace(host)
           && Uri.CheckHostName(host.Trim().TrimEnd('.')) != UriHostNameType.Unknown;
}
