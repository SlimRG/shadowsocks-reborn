using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Shadowsocks.Controller.Traffic.Applications
{
    /// <summary>
    /// Resolves a local TCP connection to its owning process without a driver.
    /// This is used by User Mode after the application has connected to the local
    /// HTTP proxy. Protected processes may expose PID/name but not their full path.
    /// </summary>
    public sealed class TcpProcessResolver
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const uint ErrorInsufficientBuffer = 122;

        public ProcessIdentity Resolve(Socket acceptedSocket)
        {
            if (acceptedSocket?.RemoteEndPoint is not IPEndPoint client
                || acceptedSocket.LocalEndPoint is not IPEndPoint proxy)
            {
                return ProcessIdentity.Unknown;
            }

            int processId = client.AddressFamily switch
            {
                AddressFamily.InterNetwork => FindOwnerIpv4(client, proxy),
                AddressFamily.InterNetworkV6 => FindOwnerIpv6(client, proxy),
                _ => 0,
            };

            return GetIdentity(processId);
        }

        private static int FindOwnerIpv4(IPEndPoint client, IPEndPoint proxy)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint result = NativeMethods.GetExtendedTcpTable(
                    IntPtr.Zero,
                    ref size,
                    false,
                    AfInet,
                    TcpTableClass.TcpTableOwnerPidAll,
                    0);

                if (result != ErrorInsufficientBuffer || size <= 0)
                {
                    return 0;
                }

                buffer = Marshal.AllocHGlobal(size);
                result = NativeMethods.GetExtendedTcpTable(
                    buffer,
                    ref size,
                    false,
                    AfInet,
                    TcpTableClass.TcpTableOwnerPidAll,
                    0);
                if (result != 0)
                {
                    return 0;
                }

                int count = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, sizeof(uint));
                int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                uint clientAddress = ToIpv4UInt32(client.Address);
                uint proxyAddress = ToIpv4UInt32(proxy.Address);

                for (int i = 0; i < count; i++)
                {
                    MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    if (row.LocalAddr == clientAddress
                        && DecodePort(row.LocalPort) == client.Port
                        && row.RemoteAddr == proxyAddress
                        && DecodePort(row.RemotePort) == proxy.Port)
                    {
                        return unchecked((int)row.OwningPid);
                    }

                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return 0;
        }

        private static int FindOwnerIpv6(IPEndPoint client, IPEndPoint proxy)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int size = 0;
                uint result = NativeMethods.GetExtendedTcpTable(
                    IntPtr.Zero,
                    ref size,
                    false,
                    AfInet6,
                    TcpTableClass.TcpTableOwnerPidAll,
                    0);

                if (result != ErrorInsufficientBuffer || size <= 0)
                {
                    return 0;
                }

                buffer = Marshal.AllocHGlobal(size);
                result = NativeMethods.GetExtendedTcpTable(
                    buffer,
                    ref size,
                    false,
                    AfInet6,
                    TcpTableClass.TcpTableOwnerPidAll,
                    0);
                if (result != 0)
                {
                    return 0;
                }

                int count = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = IntPtr.Add(buffer, sizeof(uint));
                int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                byte[] clientAddress = client.Address.GetAddressBytes();
                byte[] proxyAddress = proxy.Address.GetAddressBytes();

                for (int i = 0; i < count; i++)
                {
                    MibTcp6RowOwnerPid row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                    if (row.LocalAddr.AsSpan().SequenceEqual(clientAddress)
                        && DecodePort(row.LocalPort) == client.Port
                        && row.RemoteAddr.AsSpan().SequenceEqual(proxyAddress)
                        && DecodePort(row.RemotePort) == proxy.Port)
                    {
                        return unchecked((int)row.OwningPid);
                    }

                    rowPtr = IntPtr.Add(rowPtr, rowSize);
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return 0;
        }

        private static ProcessIdentity GetIdentity(int processId)
        {
            if (processId <= 0)
            {
                return ProcessIdentity.Unknown;
            }

            try
            {
                using Process process = Process.GetProcessById(processId);
                string path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Some protected/elevated processes deny MainModule to a normal user.
                }

                string name;
                try
                {
                    name = process.ProcessName;
                }
                catch
                {
                    name = null;
                }

                return new ProcessIdentity(processId, path, name);
            }
            catch
            {
                return new ProcessIdentity(processId, null, null);
            }
        }

        private static uint ToIpv4UInt32(IPAddress address)
        {
            byte[] bytes = address.MapToIPv4().GetAddressBytes();
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        private static int DecodePort(uint encoded)
        {
            ushort networkOrder = unchecked((ushort)encoded);
            return unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)networkOrder)));
        }

        private enum TcpTableClass
        {
            TcpTableBasicListener,
            TcpTableBasicConnections,
            TcpTableBasicAll,
            TcpTableOwnerPidListener,
            TcpTableOwnerPidConnections,
            TcpTableOwnerPidAll,
            TcpTableOwnerModuleListener,
            TcpTableOwnerModuleConnections,
            TcpTableOwnerModuleAll,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort;
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcp6RowOwnerPid
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddr;
            public uint LocalScopeId;
            public uint LocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddr;
            public uint RemoteScopeId;
            public uint RemotePort;
            public uint State;
            public uint OwningPid;
        }

        private static class NativeMethods
        {
            [DllImport("iphlpapi.dll", SetLastError = true)]
            internal static extern uint GetExtendedTcpTable(
                IntPtr tcpTable,
                ref int size,
                [MarshalAs(UnmanagedType.Bool)] bool order,
                int addressFamily,
                TcpTableClass tableClass,
                uint reserved);
        }
    }

    public readonly record struct ProcessIdentity(int ProcessId, string ProcessPath, string ProcessName)
    {
        public static ProcessIdentity Unknown => new(0, null, null);
        public bool IsCurrentProcess => ProcessId == Environment.ProcessId;
    }
}
