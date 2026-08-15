using System;
using System.IO.Pipes;
using System.Net;
using System.Text;
using Shadowsocks.Util;

namespace Shadowsocks.Controller
{
    public sealed class RequestAddUrlEventArgs : EventArgs
    {
        public RequestAddUrlEventArgs(string url)
        {
            Url = url;
        }

        public string Url { get; }
    }

    public class IPCService
    {
        private const int Int32Length = 4;
        private const int OpenUrlOpcode = 1;
        private static readonly string PipePath = $"shadowsocks-reborn\\{Utils.GetDeterministicHashCode(Shadowsocks.Engine.RuntimeEnvironment.ExecutablePath)}";

        public event EventHandler<RequestAddUrlEventArgs> OpenUrlRequested;

        public async void RunServer()
        {
            byte[] buf = new byte[4096];
            while (true)
            {
                using (NamedPipeServerStream stream = new NamedPipeServerStream(PipePath))
                {
                    await stream.WaitForConnectionAsync();
                    await stream.ReadExactlyAsync(buf.AsMemory(0, Int32Length));
                    int opcode = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buf, 0));
                    if (opcode == OpenUrlOpcode)
                    {
                        await stream.ReadExactlyAsync(buf.AsMemory(0, Int32Length));
                        int strlen = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buf, 0));
                        if (strlen < 0 || strlen > buf.Length)
                        {
                            continue;
                        }

                        await stream.ReadExactlyAsync(buf.AsMemory(0, strlen));
                        string url = Encoding.UTF8.GetString(buf, 0, strlen);

                        OpenUrlRequested?.Invoke(this, new RequestAddUrlEventArgs(url));
                    }
                }
            }
        }

        private static (NamedPipeClientStream, bool) TryConnect()
        {
            NamedPipeClientStream pipe = new NamedPipeClientStream(PipePath);
            bool exist;
            try
            {
                pipe.Connect(10);
                exist = true;
            }
            catch (TimeoutException)
            {
                exist = false;
            }
            return (pipe, exist);
        }

        public static void RequestOpenUrl(string url)
        {
            (NamedPipeClientStream pipe, bool exists) = TryConnect();
            using (pipe)
            {
                if (!exists)
                {
                    return;
                }

                byte[] opcode = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(OpenUrlOpcode));
                pipe.Write(opcode, 0, Int32Length);

                byte[] urlBytes = Encoding.UTF8.GetBytes(url);
                byte[] length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(urlBytes.Length));
                pipe.Write(length, 0, Int32Length);
                pipe.Write(urlBytes, 0, urlBytes.Length);
            }
        }
    }
}
