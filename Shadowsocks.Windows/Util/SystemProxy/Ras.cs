using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shadowsocks.Util.SystemProxy;

internal static partial class Ras
{
    private const int MaxEntryName = 256;
    private const int MaxPath = 260;
    private const uint Success = 0;
    private const uint ErrorBufferTooSmall = 603;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RasEntryName
    {
        public int Size;
        public fixed char EntryName[MaxEntryName + 1];
        public int Flags;
        public fixed char PhonebookPath[MaxPath + 1];
    }

    [LibraryImport("rasapi32.dll", EntryPoint = "RasEnumEntriesW")]
    private static unsafe partial uint RasEnumEntries(
        char* reserved,
        char* phonebook,
        RasEntryName* entries,
        ref int bufferSize,
        out int entryCount);

    public static unsafe string[] GetAllConnections()
    {
        var bufferSize = 0;
        uint result = RasEnumEntries(null, null, null, ref bufferSize, out int entryCount);
        if (result == Success && entryCount == 0)
        {
            return [];
        }

        if (result != ErrorBufferTooSmall)
        {
            throw new Win32Exception((int)result);
        }

        var entries = new RasEntryName[entryCount];
        var entrySize = sizeof(RasEntryName);
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].Size = entrySize;
        }

        fixed (RasEntryName* entriesPointer = entries)
        {
            result = RasEnumEntries(null, null, entriesPointer, ref bufferSize, out entryCount);
            if (result != Success)
            {
                throw new Win32Exception((int)result);
            }

            var names = new List<string>(entryCount);
            for (var i = 0; i < entryCount && i < entries.Length; i++)
            {
                var entryName = new string(entriesPointer[i].EntryName);
                if (!string.IsNullOrWhiteSpace(entryName))
                {
                    names.Add(entryName);
                }
            }

            return [.. names];
        }
    }
}
