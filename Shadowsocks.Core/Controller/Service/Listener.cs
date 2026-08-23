using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NLog;
using Shadowsocks.Core;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    public sealed class Listener : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public interface IService
        {
            bool Handle(byte[] firstPacket, int length, Socket socket, object state);

            void Shutdown();
        }

        public abstract class Service : IService
        {
            public abstract bool Handle(byte[] firstPacket, int length, Socket socket, object state);

            public virtual void Shutdown() { }
        }

        public sealed class UDPState
        {
            public UDPState(Socket socket)
            {
                Socket = socket;
                Buffer = new byte[4096];
                RemoteEndPoint = new IPEndPoint(
                    socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any,
                    0);
            }

            public Socket Socket { get; }
            public byte[] Buffer { get; }
            public EndPoint RemoteEndPoint { get; set; }
        }

        private sealed record ReceiveState(Socket Socket, byte[] Buffer);

        private readonly List<IService> _services;
        private Configuration _config;
        private bool _shareOverLAN;
        private Socket _tcpSocket;
        private Socket _udpSocket;
        private bool _disposed;

        public Listener(List<IService> services)
        {
            ArgumentNullException.ThrowIfNull(services);
            _services = services;
        }

        private static bool CheckIfPortInUse(int port)
        {
            IPGlobalProperties ipProperties = IPGlobalProperties.GetIPGlobalProperties();
            return ipProperties.GetActiveTcpListeners().Any(endPoint => endPoint.Port == port);
        }

        public void Start(Configuration config)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(config);

            _config = config;
            _shareOverLAN = config.shareOverLan;

            if (CheckIfPortInUse(_config.localPort))
            {
                throw new InvalidOperationException(I18N.GetString("Port {0} already in use", _config.localPort));
            }

            try
            {
                AddressFamily addressFamily = config.isIPv6Enabled
                    ? AddressFamily.InterNetworkV6
                    : AddressFamily.InterNetwork;
                _tcpSocket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                _udpSocket = new Socket(addressFamily, SocketType.Dgram, ProtocolType.Udp);
                _tcpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                IPAddress bindAddress = _shareOverLAN
                    ? (config.isIPv6Enabled ? IPAddress.IPv6Any : IPAddress.Any)
                    : (config.isIPv6Enabled ? IPAddress.IPv6Loopback : IPAddress.Loopback);
                IPEndPoint localEndPoint = new(bindAddress, _config.localPort);

                _tcpSocket.Bind(localEndPoint);
                _udpSocket.Bind(localEndPoint);
                _tcpSocket.Listen(1024);

                logger.Info($"Shadowsocks started ({ApplicationInfo.Version})");
                logger.Debug(Encryption.EncryptorFactory.DumpRegisteredEncryptor());
                _tcpSocket.BeginAccept(AcceptCallback, _tcpSocket);

                UDPState udpState = new(_udpSocket);
                EndPoint remoteEndPoint = udpState.RemoteEndPoint;
                _udpSocket.BeginReceiveFrom(
                    udpState.Buffer, 0, udpState.Buffer.Length, SocketFlags.None, ref remoteEndPoint, RecvFromCallback, udpState);
                udpState.RemoteEndPoint = remoteEndPoint;
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            Socket tcpSocket = _tcpSocket;
            _tcpSocket = null;
            tcpSocket?.Dispose();

            Socket udpSocket = _udpSocket;
            _udpSocket = null;
            udpSocket?.Dispose();

            foreach (IService service in _services)
            {
                service.Shutdown();
            }
        }

        public void RecvFromCallback(IAsyncResult ar)
        {
            if (ar.AsyncState is not UDPState state)
            {
                return;
            }

            Socket socket = state.Socket;
            try
            {
                EndPoint remoteEndPoint = state.RemoteEndPoint;
                int bytesRead = socket.EndReceiveFrom(ar, ref remoteEndPoint);
                state.RemoteEndPoint = remoteEndPoint;
                foreach (IService service in _services)
                {
                    if (service.Handle(state.Buffer, bytesRead, socket, state))
                    {
                        break;
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException e) when (IsExpectedSocketShutdown(e)) { }
            catch (Exception ex)
            {
                logger.Debug(ex);
            }
            finally
            {
                if (ReferenceEquals(socket, _udpSocket))
                {
                    try
                    {
                        EndPoint remoteEndPoint = state.RemoteEndPoint;
                        socket.BeginReceiveFrom(
                            state.Buffer, 0, state.Buffer.Length, SocketFlags.None, ref remoteEndPoint, RecvFromCallback, state);
                        state.RemoteEndPoint = remoteEndPoint;
                    }
                    catch (ObjectDisposedException) { }
                    catch (SocketException e) when (IsExpectedSocketShutdown(e)) { }
                    catch (Exception ex) { logger.Debug(ex); }
                }
            }
        }

        public void AcceptCallback(IAsyncResult ar)
        {
            if (ar.AsyncState is not Socket listener)
            {
                return;
            }

            Socket connection = null;
            try
            {
                connection = listener.EndAccept(ar);
                byte[] buffer = new byte[4096];
                var state = new ReceiveState(connection, buffer);
                connection.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, ReceiveCallback, state);
                connection = null; // ownership transferred to ReceiveCallback
            }
            catch (ObjectDisposedException) { }
            catch (SocketException e) when (IsExpectedSocketShutdown(e)) { }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
            }
            finally
            {
                connection?.Dispose();
                if (ReferenceEquals(listener, _tcpSocket))
                {
                    try { listener.BeginAccept(AcceptCallback, listener); }
                    catch (ObjectDisposedException) { }
                    catch (SocketException e) when (IsExpectedSocketShutdown(e)) { }
                    catch (Exception e) { logger.LogUsefulException(e); }
                }
            }
        }

        private static bool IsExpectedSocketShutdown(SocketException exception) =>
            exception.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted;

        private void ReceiveCallback(IAsyncResult ar)
        {
            if (ar.AsyncState is not ReceiveState state)
            {
                return;
            }

            Socket connection = state.Socket;
            try
            {
                int bytesRead = connection.EndReceive(ar);
                if (bytesRead > 0)
                {
                    foreach (IService service in _services)
                    {
                        if (service.Handle(state.Buffer, bytesRead, connection, null))
                        {
                            return;
                        }
                    }
                }

                connection.Dispose();
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
                connection.Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
