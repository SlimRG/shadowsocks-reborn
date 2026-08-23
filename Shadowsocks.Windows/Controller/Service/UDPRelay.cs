using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NLog;
using Shadowsocks.Controller.Strategy;
using Shadowsocks.Encryption;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    internal sealed class UDPRelay : Listener.Service, IDisposable
    {
        private const int MaxUdpAssociations = 512;
        private readonly ShadowsocksController _controller;
        private readonly UdpAssociationCache _cache = new(MaxUdpAssociations);
        private bool _disposed;

        public UDPRelay(ShadowsocksController controller)
        {
            this._controller = controller;
        }

        public override bool Handle(byte[] firstPacket, int length, Socket socket, object state)
        {
            if (socket.ProtocolType != ProtocolType.Udp)
            {
                return false;
            }
            if (length < 4)
            {
                return false;
            }
            Listener.UDPState udpState = (Listener.UDPState)state;
            IPEndPoint remoteEndPoint = (IPEndPoint)udpState.RemoteEndPoint;
            if (firstPacket[0] != 0 || firstPacket[1] != 0 || firstPacket[2] != 0)
            {
                return true; // SOCKS5 UDP fragmentation is intentionally unsupported; drop invalid/fragmented datagrams.
            }

            EndPoint destination = TryParseDestination(firstPacket, length);
            if (destination == null)
            {
                return true;
            }

            UDPHandler handler = _cache.Get(remoteEndPoint);
            if (handler == null || handler.IsClosed)
            {
                Server server = _controller.GetAServer(IStrategyCallerType.UDP, remoteEndPoint, destination);
                if (server?.IsConfigured != true)
                {
                    return true;
                }

                handler = new UDPHandler(_controller, socket, server, remoteEndPoint);
                handler.Receive();
                _cache.AddOrReplace(remoteEndPoint, handler);
            }
            handler.Send(firstPacket, length);
            return true;
        }

        public override void Shutdown()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _cache.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        internal static EndPoint TryParseDestination(byte[] packet, int length)
        {
            if (packet == null || length < 7 || length > packet.Length)
            {
                return null;
            }

            int offset = 3;
            byte addressType = packet[offset++];
            string host;
            switch (addressType)
            {
                case 0x01:
                    if (length < offset + 4 + 2) return null;
                    host = new IPAddress(packet.AsSpan(offset, 4)).ToString();
                    offset += 4;
                    break;
                case 0x04:
                    if (length < offset + 16 + 2) return null;
                    host = new IPAddress(packet.AsSpan(offset, 16)).ToString();
                    offset += 16;
                    break;
                case 0x03:
                    if (length < offset + 1) return null;
                    int hostLength = packet[offset++];
                    if (hostLength <= 0 || length < offset + hostLength + 2) return null;
                    host = Encoding.ASCII.GetString(packet, offset, hostLength);
                    offset += hostLength;
                    break;
                default:
                    return null;
            }

            int port = (packet[offset] << 8) | packet[offset + 1];
            if (port <= 0)
            {
                return null;
            }

            return IPAddress.TryParse(host, out IPAddress address)
                ? new IPEndPoint(address, port)
                : new DnsEndPoint(host, port);
        }

        public sealed class UDPHandler : IDisposable
        {
            private static readonly Logger logger = LogManager.GetCurrentClassLogger();

            private readonly Socket _local;
            private Socket _remote;

            private readonly Server _server;
            private readonly byte[] _buffer = new byte[65536];

            private readonly IPEndPoint _localEndPoint;
            private readonly IPEndPoint _remoteEndPoint;

            public bool IsClosed => _remote == null;

            private static IPAddress GetBindAddress(AddressFamily addressFamily)
            {
                return addressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any;
            }

            public UDPHandler(ShadowsocksController controller, Socket local, Server server, IPEndPoint localEndPoint)
            {
                _local = local;
                _server = server;
                _localEndPoint = localEndPoint;

                _remoteEndPoint = controller.ResolveOutboundEndpoint(server.server, server.ServerPort);
                _remote = new Socket(_remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _remote.Bind(new IPEndPoint(GetBindAddress(_remote.AddressFamily), 0));
            }

            public void Send(byte[] data, int length)
            {
                Socket remote = _remote;
                if (remote == null)
                {
                    return;
                }

                using IEncryptor encryptor = EncryptorFactory.GetEncryptor(_server.method, _server.password);
                byte[] dataIn = new byte[length - 3];
                Array.Copy(data, 3, dataIn, 0, length - 3);
                byte[] dataOut = new byte[65536];  // enough space for AEAD ciphers
                int outlen;
                encryptor.EncryptUDP(dataIn, length - 3, dataOut, out outlen);
                logger.Debug(_localEndPoint, _remoteEndPoint, outlen, "UDP Relay");
                remote.SendTo(dataOut, outlen, SocketFlags.None, _remoteEndPoint);
            }

            public void Receive()
            {
                Socket remote = _remote;
                if (remote == null)
                {
                    return;
                }

                EndPoint remoteEndPoint = new IPEndPoint(GetBindAddress(remote.AddressFamily), 0);
                logger.Debug("UDP relay waiting for server responses on {0}.", remote.LocalEndPoint);
                remote.BeginReceiveFrom(_buffer, 0, _buffer.Length, 0, ref remoteEndPoint, RecvFromCallback, null);
            }

            public void RecvFromCallback(IAsyncResult ar)
            {
                try
                {
                    Socket remote = _remote;
                    if (remote == null) return;
                    EndPoint remoteEndPoint = new IPEndPoint(GetBindAddress(remote.AddressFamily), 0);
                    int bytesRead = remote.EndReceiveFrom(ar, ref remoteEndPoint);

                    byte[] dataOut = new byte[bytesRead];
                    int outlen;

                    using IEncryptor encryptor = EncryptorFactory.GetEncryptor(_server.method, _server.password);
                    encryptor.DecryptUDP(_buffer, bytesRead, dataOut, out outlen);

                    byte[] sendBuf = new byte[outlen + 3];
                    Array.Copy(dataOut, 0, sendBuf, 3, outlen);

                    logger.Debug(_localEndPoint, _remoteEndPoint, outlen, "UDP Relay");
                    _local?.SendTo(sendBuf, outlen + 3, 0, _localEndPoint);

                    Receive();
                }
                catch (ObjectDisposedException)
                {
                    Close();
                }
                catch (Exception exception)
                {
                    logger.LogUsefulException(exception);
                    Close();
                }
            }

            public void Close()
            {
                Socket remote = Interlocked.Exchange(ref _remote, null);
                if (remote == null)
                {
                    return;
                }

                try
                {
                    remote.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    logger.LogUsefulException(exception);
                }
            }

            public void Dispose()
            {
                Close();
                GC.SuppressFinalize(this);
            }
        }
    }

    internal sealed class UdpAssociationCache : IDisposable
    {
        private readonly int _capacity;
        private readonly object _sync = new();
        private readonly Dictionary<IPEndPoint, LinkedListNode<Entry>> _entries = new();
        private readonly LinkedList<Entry> _lru = new();

        public UdpAssociationCache(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            _capacity = capacity;
        }

        public UDPRelay.UDPHandler Get(IPEndPoint key)
        {
            lock (_sync)
            {
                if (!_entries.TryGetValue(key, out LinkedListNode<Entry> node))
                {
                    return null;
                }

                _lru.Remove(node);
                _lru.AddLast(node);
                return node.Value.Handler;
            }
        }

        public void AddOrReplace(IPEndPoint key, UDPRelay.UDPHandler handler)
        {
            UDPRelay.UDPHandler toClose = null;
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out LinkedListNode<Entry> existing))
                {
                    _lru.Remove(existing);
                    _entries.Remove(key);
                    toClose = existing.Value.Handler;
                }
                else if (_entries.Count >= _capacity && _lru.First is LinkedListNode<Entry> oldest)
                {
                    _lru.RemoveFirst();
                    _entries.Remove(oldest.Value.Key);
                    toClose = oldest.Value.Handler;
                }

                var entry = new Entry(key, handler);
                var node = new LinkedListNode<Entry>(entry);
                _lru.AddLast(node);
                _entries.Add(key, node);
            }

            toClose?.Dispose();
        }

        public void Dispose()
        {
            List<UDPRelay.UDPHandler> handlers;
            lock (_sync)
            {
                handlers = new List<UDPRelay.UDPHandler>(_entries.Count);
                foreach (LinkedListNode<Entry> node in _entries.Values)
                {
                    handlers.Add(node.Value.Handler);
                }

                _entries.Clear();
                _lru.Clear();
            }

            foreach (UDPRelay.UDPHandler handler in handlers)
            {
                handler.Dispose();
            }

            GC.SuppressFinalize(this);
        }

        private sealed class Entry
        {
            public Entry(IPEndPoint key, UDPRelay.UDPHandler handler)
            {
                Key = key;
                Handler = handler;
            }

            public IPEndPoint Key { get; }
            public UDPRelay.UDPHandler Handler { get; }
        }
    }
}
