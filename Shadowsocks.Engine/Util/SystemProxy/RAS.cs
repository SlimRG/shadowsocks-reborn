using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shadowsocks.Util.SystemProxy
{
    internal static class RAS
    {
        private const int MaxEntryName = 256;
        private const int MaxPath = 260;
        private const uint Success = 0;
        private const uint ErrorBufferTooSmall = 603;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RasEntryName
        {
            public int dwSize;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxEntryName + 1)]
            public string szEntryName;

            public int dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath + 1)]
            public string szPhonebookPath;
        }

        [DllImport("rasapi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RasEnumEntries(
            string reserved,
            string phonebook,
            [In, Out] RasEntryName[] entries,
            ref int bufferSize,
            out int entryCount);

        public static string[] GetAllConnections()
        {
            int bufferSize = 0;
            uint result = RasEnumEntries(null, null, null, ref bufferSize, out int entryCount);
            if (result == Success && entryCount == 0)
            {
                return [];
            }

            if (result != ErrorBufferTooSmall)
            {
                throw new Win32Exception((int)result);
            }

            RasEntryName[] entries = new RasEntryName[entryCount];
            int entrySize = Marshal.SizeOf<RasEntryName>();
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].dwSize = entrySize;
            }

            result = RasEnumEntries(null, null, entries, ref bufferSize, out entryCount);
            if (result != Success)
            {
                throw new Win32Exception((int)result);
            }

            List<string> names = new(entryCount);
            for (int i = 0; i < entryCount && i < entries.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(entries[i].szEntryName))
                {
                    names.Add(entries[i].szEntryName);
                }
            }

            return [.. names];
        }
    }
}
