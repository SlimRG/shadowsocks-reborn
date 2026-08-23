using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Shadowsocks.NetworkService.Routing;

internal readonly record struct ProcessIdentity(int ProcessId, string? ProcessPath, string? ProcessName)
{
    public static ProcessIdentity Unknown => new(0, null, null);
}

internal static partial class ProcessResolver
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const int DnsOwnerLookupAttempts = 4;

    public static ProcessIdentity Resolve(FlowKey flow)
    {
        int pid;
        if (!ProcessAttributionCache.TryGetProcessId(flow.Protocol, flow.LocalPort, DateTime.UtcNow, out pid))
        {
            pid = ResolvePidWithRetry(
                () => FindOwnerPid(flow),
                flow.RemotePort == 53,
                static delayMilliseconds => Thread.Sleep(delayMilliseconds));
        }

        if (pid <= 0)
        {
            return ProcessIdentity.Unknown;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
            }

            string? name = null;
            try
            {
                name = process.ProcessName;
            }
            catch
            {
            }

            return new ProcessIdentity(pid, path, name);
        }
        catch
        {
            return new ProcessIdentity(pid, null, null);
        }
    }

    private static int FindOwnerPid(FlowKey flow)
    {
        return flow.Protocol switch
        {
            6 => flow.LocalAddress.AddressFamily == AddressFamily.InterNetwork
                ? FindTcp4(flow)
                : FindTcp6(flow),
            17 => flow.LocalAddress.AddressFamily == AddressFamily.InterNetwork
                ? FindUdp4(flow)
                : FindUdp6(flow),
            _ => 0,
        };
    }

    internal static int ResolvePidWithRetry(
        Func<int> resolvePid,
        bool dnsFlow,
        Action<int>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(resolvePid);
        delay ??= static milliseconds => Thread.Sleep(milliseconds);

        int attempts = dnsFlow ? DnsOwnerLookupAttempts : 1;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int processId = resolvePid();
            if (processId > 0)
            {
                return processId;
            }

            if (attempt + 1 < attempts)
            {
                // DNSCrypt's short-lived -check process and a newly restarted runtime can emit
                // bootstrap DNS immediately after creating their socket. Give the Windows owner
                // tables a few milliseconds to expose the PID before applying port-53 policy.
                delay(attempt switch { 0 => 1, 1 => 4, _ => 10 });
            }
        }

        return 0;
    }

    private static int FindTcp4(FlowKey flow)
    {
        nint buffer = 0;
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedTcpTable(0, ref size, false, AfInet, TcpTableClass.OwnerPidAll, 0);
            if (result != ErrorInsufficientBuffer || size <= 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableClass.OwnerPidAll, 0);
            if (result != 0)
            {
                return 0;
            }

            uint localAddr = ToIpv4UInt32(flow.LocalAddress);
            uint remoteAddr = ToIpv4UInt32(flow.RemoteAddress);
            int count = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibTcpRowOwnerPid row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                if (row.LocalAddr == localAddr
                    && DecodePort(row.LocalPort) == flow.LocalPort
                    && row.RemoteAddr == remoteAddr
                    && DecodePort(row.RemotePort) == flow.RemotePort)
                {
                    return unchecked((int)row.OwningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return 0;
    }

    private static int FindTcp6(FlowKey flow)
    {
        nint buffer = 0;
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedTcpTable(0, ref size, false, AfInet6, TcpTableClass.OwnerPidAll, 0);
            if (result != ErrorInsufficientBuffer || size <= 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedTcpTable(buffer, ref size, false, AfInet6, TcpTableClass.OwnerPidAll, 0);
            if (result != 0)
            {
                return 0;
            }

            byte[] local = flow.LocalAddress.GetAddressBytes();
            byte[] remote = flow.RemoteAddress.GetAddressBytes();
            int count = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibTcp6RowOwnerPid row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                if (row.LocalAddr.AsSpan().SequenceEqual(local)
                    && DecodePort(row.LocalPort) == flow.LocalPort
                    && row.RemoteAddr.AsSpan().SequenceEqual(remote)
                    && DecodePort(row.RemotePort) == flow.RemotePort)
                {
                    return unchecked((int)row.OwningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return 0;
    }

    private static int FindUdp4(FlowKey flow)
    {
        nint buffer = 0;
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedUdpTable(0, ref size, false, AfInet, UdpTableClass.OwnerPid, 0);
            if (result != ErrorInsufficientBuffer || size <= 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedUdpTable(buffer, ref size, false, AfInet, UdpTableClass.OwnerPid, 0);
            if (result != 0)
            {
                return 0;
            }

            uint localAddr = ToIpv4UInt32(flow.LocalAddress);
            int count = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibUdpRowOwnerPid row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPtr);
                if ((row.LocalAddr == 0 || row.LocalAddr == localAddr)
                    && DecodePort(row.LocalPort) == flow.LocalPort)
                {
                    return unchecked((int)row.OwningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return 0;
    }

    private static int FindUdp6(FlowKey flow)
    {
        nint buffer = 0;
        try
        {
            int size = 0;
            uint result = NativeMethods.GetExtendedUdpTable(0, ref size, false, AfInet6, UdpTableClass.OwnerPid, 0);
            if (result != ErrorInsufficientBuffer || size <= 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal(size);
            result = NativeMethods.GetExtendedUdpTable(buffer, ref size, false, AfInet6, UdpTableClass.OwnerPid, 0);
            if (result != 0)
            {
                return 0;
            }

            byte[] local = flow.LocalAddress.GetAddressBytes();
            int count = Marshal.ReadInt32(buffer);
            nint rowPtr = buffer + sizeof(uint);
            int rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
            for (int i = 0; i < count; i++, rowPtr += rowSize)
            {
                MibUdp6RowOwnerPid row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPtr);
                bool any = row.LocalAddr.All(static b => b == 0);
                if ((any || row.LocalAddr.AsSpan().SequenceEqual(local))
                    && DecodePort(row.LocalPort) == flow.LocalPort)
                {
                    return unchecked((int)row.OwningPid);
                }
            }
        }
        finally
        {
            if (buffer != 0)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return 0;
    }

    private static uint ToIpv4UInt32(IPAddress address)
        => BinaryPrimitives.ReadUInt32LittleEndian(address.MapToIPv4().GetAddressBytes());

    private static ushort DecodePort(uint encoded)
    {
        ushort networkOrder = unchecked((ushort)encoded);
        return unchecked((ushort)IPAddress.NetworkToHostOrder(unchecked((short)networkOrder)));
    }

    private enum TcpTableClass
    {
        BasicListener,
        BasicConnections,
        BasicAll,
        OwnerPidListener,
        OwnerPidConnections,
        OwnerPidAll,
        OwnerModuleListener,
        OwnerModuleConnections,
        OwnerModuleAll,
    }

    private enum UdpTableClass
    {
        Basic,
        OwnerPid,
        OwnerModule,
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
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdpRowOwnerPid
    {
        public uint LocalAddr;
        public uint LocalPort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        public uint OwningPid;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("iphlpapi.dll", SetLastError = true)]
        internal static partial uint GetExtendedTcpTable(
            nint table,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            TcpTableClass tableClass,
            uint reserved);

        [LibraryImport("iphlpapi.dll", SetLastError = true)]
        internal static partial uint GetExtendedUdpTable(
            nint table,
            ref int size,
            [MarshalAs(UnmanagedType.Bool)] bool order,
            int addressFamily,
            UdpTableClass tableClass,
            uint reserved);
    }
}
