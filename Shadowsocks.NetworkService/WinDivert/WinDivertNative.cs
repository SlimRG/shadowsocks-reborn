using System.Reflection;
using System.Runtime.InteropServices;

namespace Shadowsocks.NetworkService.WinDivert;

internal enum WinDivertLayer : int
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

internal enum WinDivertEvent : byte
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
    ReflectOpen = 8,
    ReflectClose = 9,
}

[Flags]
internal enum WinDivertFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    ReceiveOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

internal enum WinDivertShutdown : int
{
    Receive = 0x1,
    Send = 0x2,
    Both = 0x3,
}

internal enum WinDivertParam : int
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
    VersionMajor = 3,
    VersionMinor = 4,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinDivertDataNetwork
{
    public uint IfIdx;
    public uint SubIfIdx;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WinDivertDataFlow
{
    public ulong EndpointId;
    public ulong ParentEndpointId;
    public uint ProcessId;
    public fixed uint LocalAddr[4];
    public fixed uint RemoteAddr[4];
    public ushort LocalPort;
    public ushort RemotePort;
    public byte Protocol;
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
internal unsafe struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint BitFields;
    [FieldOffset(12)] public uint Reserved2;
    [FieldOffset(16)] public WinDivertDataNetwork Network;
    [FieldOffset(16)] public WinDivertDataFlow Flow;
    [FieldOffset(16)] public fixed byte Data[64];

    public readonly WinDivertEvent Event => (WinDivertEvent)((BitFields >> 8) & 0xFF);

    public bool Outbound
    {
        readonly get => (BitFields & (1u << 17)) != 0;
        set
        {
            if (value)
            {
                BitFields |= 1u << 17;
            }
            else
            {
                BitFields &= ~(1u << 17);
            }
        }
    }

    public readonly bool IPv6 => (BitFields & (1u << 20)) != 0;
}

internal static unsafe partial class WinDivertNative
{
    private static string? _directory;
    private static nint _loadedModule;
    private static int _resolverInstalled;

    internal static void ConfigureDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullPath = Path.GetFullPath(directory);
        string dllPath = Path.Combine(fullPath, "WinDivert.dll");
        string driverPath = Path.Combine(fullPath, "WinDivert64.sys");
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("WinDivert.dll was not found.", dllPath);
        }
        if (!File.Exists(driverPath))
        {
            throw new FileNotFoundException("WinDivert64.sys was not found.", driverPath);
        }

        _directory = fullPath;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(WinDivertNative).Assembly, ResolveLibrary);
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "WinDivert.dll", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (_loadedModule != 0)
        {
            return _loadedModule;
        }

        string directory = _directory ?? throw new InvalidOperationException("WinDivert directory is not configured.");
        _loadedModule = NativeLibrary.Load(Path.Combine(directory, "WinDivert.dll"));
        return _loadedModule;
    }

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertOpen", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial nint Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertRecv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Receive(nint handle, void* packet, uint packetLength, out uint receivedLength, out WinDivertAddress address);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertSend", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Send(nint handle, void* packet, uint packetLength, out uint sentLength, in WinDivertAddress address);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertShutdown", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shutdown(nint handle, WinDivertShutdown how);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertClose", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Close(nint handle);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertSetParam", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetParam(nint handle, WinDivertParam parameter, ulong value);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertHelperParsePacket", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ParsePacket(
        void* packet,
        uint packetLength,
        out nint ipHeader,
        out nint ipv6Header,
        out byte protocol,
        nint icmpHeader,
        nint icmpv6Header,
        out nint tcpHeader,
        out nint udpHeader,
        out nint data,
        out uint dataLength,
        nint next,
        nint nextLength);

    [LibraryImport("WinDivert.dll", EntryPoint = "WinDivertHelperCalcChecksums", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CalculateChecksums(void* packet, uint packetLength, ref WinDivertAddress address, ulong flags);

    internal static bool IsInvalidHandle(nint handle) => handle == -1 || handle == 0;
}
