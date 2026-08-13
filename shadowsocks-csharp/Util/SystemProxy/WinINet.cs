using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NLog;

namespace Shadowsocks.Util.SystemProxy
{
    [Flags]
    internal enum InternetPerConnectionFlags
    {
        Direct = 0x01,
        Proxy = 0x02,
        AutoProxyUrl = 0x04,
        AutoDetect = 0x08,
    }

    internal sealed class WinINetSetting
    {
        public InternetPerConnectionFlags Flags { get; init; } = InternetPerConnectionFlags.Direct;
        public string ProxyServer { get; init; } = string.Empty;
        public string ProxyBypass { get; init; } = string.Empty;
        public string AutoConfigUrl { get; init; } = string.Empty;
    }

    internal static class WinINet
    {
        private const int InternetOptionRefresh = 37;
        private const int InternetOptionPerConnectionOption = 75;
        private const int InternetOptionProxySettingsChanged = 95;

        private const int InternetPerConnFlags = 1;
        private const int InternetPerConnProxyServer = 2;
        private const int InternetPerConnProxyBypass = 3;
        private const int InternetPerConnAutoConfigUrl = 4;
        private const int ErrorFileNotFound = 2;
        private const int ErrorWinHttpAutoProxyServiceError = 12178;
        private const string InternetSettingsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private static readonly string[] LanBypass =
        [
            "<local>",
            "localhost",
            "127.*",
            "10.*",
            "172.16.*",
            "172.17.*",
            "172.18.*",
            "172.19.*",
            "172.20.*",
            "172.21.*",
            "172.22.*",
            "172.23.*",
            "172.24.*",
            "172.25.*",
            "172.26.*",
            "172.27.*",
            "172.28.*",
            "172.29.*",
            "172.30.*",
            "172.31.*",
            "192.168.*",
        ];

        private static WinINetSetting initialSetting;
        private static bool queryFallbackLogged;

        public static bool Operational { get; private set; } = true;

        static WinINet()
        {
            try
            {
                initialSetting = Query();
            }
            catch (DllNotFoundException ex)
            {
                Operational = false;
                initialSetting = new();
                logger.Warn(ex, "Windows proxy APIs are unavailable; system proxy integration is disabled.");
            }
            catch (EntryPointNotFoundException ex)
            {
                Operational = false;
                initialSetting = new();
                logger.Warn(ex, "Windows proxy APIs are unavailable; system proxy integration is disabled.");
            }
            catch (Win32Exception ex)
            {
                // Reading the previous state is only needed to restore it on exit. Do not
                // disable proxy writes merely because the initial snapshot could not be read.
                initialSetting = new();
                logger.Warn(ex, "Unable to snapshot the initial Windows proxy state; Direct will be restored on exit.");
            }
        }

        public static void ProxyGlobal(string server)
        {
            string bypass = BuildGlobalBypassList(initialSetting.ProxyBypass);
            ApplyAndVerify(
                [
                    SetOption.Flags(InternetPerConnectionFlags.Direct | InternetPerConnectionFlags.Proxy),
                    SetOption.String(InternetPerConnProxyServer, server),
                    SetOption.String(InternetPerConnProxyBypass, bypass),
                    SetOption.String(InternetPerConnAutoConfigUrl, string.Empty),
                ],
                setting =>
                    setting.Flags.HasFlag(InternetPerConnectionFlags.Proxy) &&
                    string.Equals(setting.ProxyServer, server, StringComparison.OrdinalIgnoreCase),
                "global proxy");
        }

        public static void ProxyPAC(string url)
        {
            ApplyAndVerify(
                [
                    SetOption.Flags(InternetPerConnectionFlags.Direct | InternetPerConnectionFlags.AutoProxyUrl),
                    SetOption.String(InternetPerConnProxyServer, string.Empty),
                    SetOption.String(InternetPerConnAutoConfigUrl, url),
                ],
                setting =>
                    setting.Flags.HasFlag(InternetPerConnectionFlags.AutoProxyUrl) &&
                    string.Equals(setting.AutoConfigUrl, url, StringComparison.OrdinalIgnoreCase),
                "PAC proxy");
        }

        public static void Restore()
        {
            Set(initialSetting);
        }

        public static void Reset()
        {
            Set(new());
        }

        public static WinINetSetting Query()
        {
            if (!Operational)
            {
                return new();
            }

            WinHttpCurrentUserIeProxyConfig native = default;
            if (!WinHttpGetIEProxyConfigForCurrentUser(ref native))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorFileNotFound &&
                    error != ErrorWinHttpAutoProxyServiceError &&
                    !queryFallbackLogged)
                {
                    queryFallbackLogged = true;
                    logger.Warn(new Win32Exception(error),
                        "Unable to read the active proxy state through WinHTTP; using the current-user Internet Settings registry as a fallback.");
                }

                return QueryRegistryFallback();
            }

            try
            {
                string proxyServer = PtrToString(native.Proxy);
                string proxyBypass = PtrToString(native.ProxyBypass);
                string autoConfigUrl = PtrToString(native.AutoConfigUrl);

                InternetPerConnectionFlags flags = InternetPerConnectionFlags.Direct;
                if (native.AutoDetect)
                {
                    flags |= InternetPerConnectionFlags.AutoDetect;
                }
                if (!string.IsNullOrWhiteSpace(proxyServer))
                {
                    flags |= InternetPerConnectionFlags.Proxy;
                }
                if (!string.IsNullOrWhiteSpace(autoConfigUrl))
                {
                    flags |= InternetPerConnectionFlags.AutoProxyUrl;
                }

                return new()
                {
                    Flags = flags,
                    ProxyServer = proxyServer,
                    ProxyBypass = proxyBypass,
                    AutoConfigUrl = autoConfigUrl,
                };
            }
            finally
            {
                FreeGlobalString(native.AutoConfigUrl);
                FreeGlobalString(native.Proxy);
                FreeGlobalString(native.ProxyBypass);
            }
        }

        private static void Set(WinINetSetting setting)
        {
            SetOption[] options =
            [
                SetOption.Flags(setting.Flags),
                SetOption.String(InternetPerConnProxyServer, setting.ProxyServer),
                SetOption.String(InternetPerConnProxyBypass, setting.ProxyBypass),
                SetOption.String(InternetPerConnAutoConfigUrl, setting.AutoConfigUrl),
            ];

            try
            {
                Apply(options);
            }
            finally
            {
                DisposeOptions(options);
            }
        }

        private static void ApplyAndVerify(SetOption[] options, Func<WinINetSetting, bool> verifier, string description)
        {
            try
            {
                Apply(options);
                if (verifier(Query()))
                {
                    return;
                }

                // A second direct WinINet notification is cheap and handles Windows races where
                // another startup component updates Internet Settings at the same moment.
                logger.Warn("WinINet did not retain the requested {0} state; applying it once more.", description);
                Apply(options);
                if (!verifier(Query()))
                {
                    throw new InvalidOperationException($"Windows did not retain the requested {description} state.");
                }
            }
            finally
            {
                DisposeOptions(options);
            }
        }

        private static void Apply(SetOption[] options)
        {
            if (!Operational)
            {
                throw new InvalidOperationException("WinINet system proxy integration is unavailable.");
            }

            // LAN is the primary Windows proxy scope. A broken or unavailable RAS
            // phonebook must not prevent the LAN proxy from being applied.
            ApplyToConnection(options, null);

            string[] connections;
            try
            {
                connections = RAS.GetAllConnections();
            }
            catch (Win32Exception ex)
            {
                logger.Warn(ex, "Unable to enumerate RAS connections; LAN proxy settings were still applied.");
                return;
            }

            foreach (string connection in connections)
            {
                try
                {
                    ApplyToConnection(options, connection);
                }
                catch (Win32Exception ex)
                {
                    logger.Warn(ex, $"Unable to update WinINet proxy settings for RAS connection '{connection}'.");
                }
            }
        }

        private static void DisposeOptions(SetOption[] options)
        {
            foreach (SetOption option in options)
            {
                option.Dispose();
            }
        }

        private static void ApplyToConnection(SetOption[] options, string connectionName)
        {
            int optionSize = Marshal.SizeOf<InternetPerConnectionOption>();
            IntPtr optionBuffer = Marshal.AllocCoTaskMem(optionSize * options.Length);
            IntPtr connection = IntPtr.Zero;
            IntPtr listBuffer = IntPtr.Zero;
            try
            {
                IntPtr current = optionBuffer;
                foreach (SetOption option in options)
                {
                    Marshal.StructureToPtr(option.Native, current, false);
                    current += optionSize;
                }

                if (!string.IsNullOrEmpty(connectionName))
                {
                    connection = Marshal.StringToHGlobalUni(connectionName);
                }

                InternetPerConnectionOptionList list = new()
                {
                    Size = Marshal.SizeOf<InternetPerConnectionOptionList>(),
                    Connection = connection,
                    OptionCount = options.Length,
                    OptionError = 0,
                    Options = optionBuffer,
                };

                listBuffer = Marshal.AllocCoTaskMem(list.Size);
                Marshal.StructureToPtr(list, listBuffer, false);

                if (!InternetSetOption(IntPtr.Zero, InternetOptionPerConnectionOption, listBuffer, list.Size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                NotifyProxySettingsChanged();
            }
            finally
            {
                if (listBuffer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(listBuffer);
                }
                if (connection != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(connection);
                }
                Marshal.FreeCoTaskMem(optionBuffer);
            }
        }

        private static void NotifyProxySettingsChanged()
        {
            if (!InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static string BuildGlobalBypassList(string original)
        {
            HashSet<string> entries = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(original))
            {
                foreach (string item in original.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    entries.Add(item);
                }
            }
            foreach (string item in LanBypass)
            {
                entries.Add(item);
            }
            return string.Join(";", entries);
        }

        private static WinINetSetting QueryRegistryFallback()
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsRegistryPath);
            if (key == null)
            {
                return new();
            }

            string proxyServer = key.GetValue("ProxyServer") as string ?? string.Empty;
            string proxyBypass = key.GetValue("ProxyOverride") as string ?? string.Empty;
            string autoConfigUrl = key.GetValue("AutoConfigURL") as string ?? string.Empty;
            bool proxyEnabled = key.GetValue("ProxyEnable") is int proxyEnable && proxyEnable != 0;

            InternetPerConnectionFlags flags = InternetPerConnectionFlags.Direct;
            if (proxyEnabled && !string.IsNullOrWhiteSpace(proxyServer))
            {
                flags |= InternetPerConnectionFlags.Proxy;
            }
            if (!string.IsNullOrWhiteSpace(autoConfigUrl))
            {
                flags |= InternetPerConnectionFlags.AutoProxyUrl;
            }

            return new()
            {
                Flags = flags,
                ProxyServer = proxyServer,
                ProxyBypass = proxyBypass,
                AutoConfigUrl = autoConfigUrl,
            };
        }

        private static string PtrToString(IntPtr value)
        {
            return value == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(value) ?? string.Empty;
        }

        private static void FreeGlobalString(IntPtr value)
        {
            if (value != IntPtr.Zero)
            {
                GlobalFree(value);
            }
        }

        private sealed class SetOption : IDisposable
        {
            private IntPtr allocatedString;

            private SetOption(InternetPerConnectionOption native, IntPtr allocatedString = default)
            {
                Native = native;
                this.allocatedString = allocatedString;
            }

            public InternetPerConnectionOption Native { get; }

            public static SetOption Flags(InternetPerConnectionFlags flags)
            {
                return new(new()
                {
                    Option = InternetPerConnFlags,
                    Value = new() { IntValue = (int)flags },
                });
            }

            public static SetOption String(int option, string value)
            {
                IntPtr pointer = Marshal.StringToHGlobalUni(value ?? string.Empty);
                return new(new()
                {
                    Option = option,
                    Value = new() { StringValue = pointer },
                }, pointer);
            }

            public void Dispose()
            {
                if (allocatedString != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(allocatedString);
                    allocatedString = IntPtr.Zero;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinHttpCurrentUserIeProxyConfig
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool AutoDetect;
            public IntPtr AutoConfigUrl;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }

        // INTERNET_PER_CONN_OPTION.Value is a native union of DWORD, pointer and FILETIME.
        // FILETIME keeps the union 8 bytes wide even in our x86 process; omitting it would
        // shrink INTERNET_PER_CONN_OPTION from 12 to 8 bytes and corrupt option arrays.
        [StructLayout(LayoutKind.Explicit, Size = 8)]
        private struct InternetPerConnectionOptionValue
        {
            [FieldOffset(0)]
            public int IntValue;

            [FieldOffset(0)]
            public IntPtr StringValue;

            [FieldOffset(0)]
            public System.Runtime.InteropServices.ComTypes.FILETIME FileTimeValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct InternetPerConnectionOption
        {
            public int Option;
            public InternetPerConnectionOptionValue Value;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InternetPerConnectionOptionList
        {
            public int Size;
            public IntPtr Connection;
            public int OptionCount;
            public int OptionError;
            public IntPtr Options;
        }

        [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

        [DllImport("winhttp.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WinHttpCurrentUserIeProxyConfig proxyConfig);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);
    }
}
