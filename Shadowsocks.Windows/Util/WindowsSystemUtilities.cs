using System;
using Microsoft.Win32;
using NLog;

namespace Shadowsocks.Util
{
    public enum WindowsThemeMode
    {
        Dark,
        Light,
    }

    /// <summary>
    /// Windows-only registry and shell preferences. Kept outside Shadowsocks.Core so
    /// the core assembly has no dependency on Microsoft.Win32.
    /// </summary>
    public static class WindowsSystemUtilities
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static WindowsThemeMode GetSystemThemeSetting()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
                object value = key?.GetValue("SystemUsesLightTheme");
                return value is int intValue && intValue != 0
                    ? WindowsThemeMode.Light
                    : WindowsThemeMode.Dark;
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Cannot read the Windows system theme; using dark mode.");
                return WindowsThemeMode.Dark;
            }
        }

        public static RegistryKey OpenRegistryKey(
            string name,
            bool writable,
            RegistryHive hive = RegistryHive.CurrentUser)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Registry key name must not be empty.", nameof(name));

            try
            {
                return RegistryKey.OpenBaseKey(
                        hive,
                        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32)
                    .OpenSubKey(name, writable);
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
                return null;
            }
        }
    }
}
