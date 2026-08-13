using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using NLog;
using Shadowsocks.Util.Sockets;

namespace Shadowsocks.Controller
{
    class PortForwarder : Listener.Service
    {
        private readonly int _targetPort;
        private readonly object _handlersLock = new();
        private readonly HashSet<Handler> _handlers = [];
        private bool _stopping;

        public PortForwarder(int targetPort)
        {
            _targetPort = targetPort;
        }

        public override bool Handle(byte[] firstPacket, int length, Socket socket, object state)
        {
            if (socket.ProtocolType != ProtocolType.Tcp)
            {
                return false;
            }

            Handler handler;
            lock (_handlersLock)
            {
                if (_stopping)
                {
                    socket.Close();
                    return true;
                }

                handler = new Handler(RemoveHandler);
                _handlers.Add(handler);
            }

            handler.Start(firstPacket, length, socket, _targetPort);
            return true;
        }

        public override void Stop()
        {
            Handler[] handlers;
            lock (_handlersLock)
            {
                _stopping = true;
                handlers = [.. _handlers];
            }

            foreach (Handler handler in handlers)
            {
                handler.Close();
            }
        }

        private void RemoveHandler(Handler handler)
        {
            lock (_handlersLock)
            {
                _handlers.Remove(handler);
            }
        }

        private class Handler
        {
            private static readonly Logger logger = LogManager.GetCurrentClassLogger();

            private readonly Action<Handler> _onClosed;
            private byte[] _firstPacket;
            private int _firstPacketLength;
            private Socket _local;
            private WrappedSocket _remote;
            private volatile bool _closed;
            private bool _localShutdown = false;
            private bool _remoteShutdown = false;
            private const int RecvSize = 2048;
            // remote receive buffer
            private byte[] remoteRecvBuffer = new byte[RecvSize];
            // connection receive buffer
            private byte[] connetionRecvBuffer = new byte[RecvSize];

            // instance-based lock
            private readonly object _Lock = new object();

            public Handler(Action<Handler> onClosed)
            {
                _onClosed = onClosed;
            }

            public void Start(byte[] firstPacket, int length, Socket socket, int targetPort)
            {
                lock (_Lock)
                {
                    if (_closed)
                    {
                        socket.Close();
                        return;
                    }

                    _firstPacket = firstPacket;
                    _firstPacketLength = length;
                    _local = socket;
                    _remote = new WrappedSocket();
                }

                try
                {
                    // Local Port Forward use IP as is
                    EndPoint remoteEP = SocketUtil.GetEndPoint(_local.AddressFamily == AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1", targetPort);

                    // Connect to the remote endpoint.
                    _remote.BeginConnect(remoteEP, ConnectCallback, null);
                }
                catch (Exception e)
                {
                    if (!_closed)
                    {
                        logger.LogUsefulException(e);
                    }
                    Close();
                }
            }

            private void ConnectCallback(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    _remote.EndConnect(ar);
                    _remote.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                    HandshakeReceive();
                }
                catch (Exception e)
                {
                    if (!_closed)
                    {
                        logger.LogUsefulException(e);
                    }
                    Close();
                }
            }

            private void HandshakeReceive()
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    _remote.BeginSend(_firstPacket, 0, _firstPacketLength, 0, StartPipe, null);
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void StartPipe(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    _remote.EndSend(ar);
                    _remote.BeginReceive(remoteRecvBuffer, 0, RecvSize, 0,
                        PipeRemoteReceiveCallback, null);
                    _local.BeginReceive(connetionRecvBuffer, 0, RecvSize, 0,
                        PipeConnectionReceiveCallback, null);
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void PipeRemoteReceiveCallback(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    int bytesRead = _remote.EndReceive(ar);
                    if (bytesRead > 0)
                    {
                        _local.BeginSend(remoteRecvBuffer, 0, bytesRead, 0, PipeConnectionSendCallback, null);
                    }
                    else
                    {
                        _local.Shutdown(SocketShutdown.Send);
                        _localShutdown = true;
                        CheckClose();
                    }
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void PipeConnectionReceiveCallback(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    int bytesRead = _local.EndReceive(ar);
                    if (bytesRead > 0)
                    {
                        _remote.BeginSend(connetionRecvBuffer, 0, bytesRead, 0, PipeRemoteSendCallback, null);
                    }
                    else
                    {
                        _remote.Shutdown(SocketShutdown.Send);
                        _remoteShutdown = true;
                        CheckClose();
                    }
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void PipeRemoteSendCallback(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    _remote.EndSend(ar);
                    _local.BeginReceive(connetionRecvBuffer, 0, RecvSize, 0,
                        PipeConnectionReceiveCallback, null);
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void PipeConnectionSendCallback(IAsyncResult ar)
            {
                if (_closed)
                {
                    return;
                }
                try
                {
                    _local.EndSend(ar);
                    _remote.BeginReceive(remoteRecvBuffer, 0, RecvSize, 0,
                        PipeRemoteReceiveCallback, null);
                }
                catch (Exception e)
                {
                    logger.LogUsefulException(e);
                    Close();
                }
            }

            private void CheckClose()
            {
                if (_localShutdown && _remoteShutdown)
                {
                    Close();
                }
            }

            public void Close()
            {
                lock (_Lock)
                {
                    if (_closed)
                    {
                        return;
                    }
                    _closed = true;
                }
                if (_local != null)
                {
                    try
                    {
                        _local.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException)
                    {
                        // Expected if the peer already closed while the forwarder is stopping.
                    }
                    finally
                    {
                        _local.Close();
                        _local = null;
                    }
                }
                if (_remote != null)
                {
                    try
                    {
                        _remote.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException)
                    {
                        // Expected if the peer already closed while the forwarder is stopping.
                    }
                    finally
                    {
                        _remote.Dispose();
                        _remote = null;
                    }
                }

                _onClosed(this);
            }
        }
    }
}
