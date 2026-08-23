using System;
using Microsoft.Win32;
using NLog;
using Shadowsocks.Core;

namespace Shadowsocks.Controller
{
    public static class ProtocolHandler
    {
        private const string SsUrlRegKey = @"SOFTWARE\Classes\ss";
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        public static bool Set(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(SsUrlRegKey, throwOnMissingSubKey: false);
                    Logger.Info(@"Successfully removed ss:// association.");
                    return true;
                }

                using RegistryKey ssUrlAssociation = Registry.CurrentUser.CreateSubKey(
                    SsUrlRegKey,
                    RegistryKeyPermissionCheck.ReadWriteSubTree);

                if (ssUrlAssociation is null)
                {
                    Logger.Error(@"Failed to create HKCU\SOFTWARE\Classes\ss to register ss:// association.");
                    return false;
                }

                ssUrlAssociation.SetValue("", "URL:shadowsocks-reborn");
                ssUrlAssociation.SetValue("URL Protocol", "");

                using RegistryKey shell = ssUrlAssociation.CreateSubKey("shell");
                using RegistryKey open = shell?.CreateSubKey("open");
                using RegistryKey command = open?.CreateSubKey("command");
                if (command is null)
                {
                    Logger.Error(@"Failed to create the ss:// shell open command registry key.");
                    return false;
                }

                command.SetValue("", BuildShellOpenCommand(RuntimeEnvironment.ExecutablePath));
                Logger.Info(@"Successfully added ss:// association.");
                return true;
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
                return false;
            }
        }

        public static bool Check()
        {
            try
            {
                using RegistryKey ssUrlAssociation = Registry.CurrentUser.OpenSubKey(SsUrlRegKey, writable: false);
                using RegistryKey command = ssUrlAssociation?
                    .OpenSubKey("shell", writable: false)?
                    .OpenSubKey("open", writable: false)?
                    .OpenSubKey("command", writable: false);

                return command?.GetValue("") is string registeredCommand
                    && string.Equals(
                        registeredCommand,
                        BuildShellOpenCommand(RuntimeEnvironment.ExecutablePath),
                        StringComparison.Ordinal);
            }
            catch (Exception exception)
            {
                Logger.LogUsefulException(exception);
                return false;
            }
        }

        internal static string BuildShellOpenCommand(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new ArgumentException("Executable path is required.", nameof(executablePath));
            }

            // Windows protocol handlers are parsed as command lines. Quote both the
            // executable path and the URL placeholder so paths/URLs containing spaces
            // cannot change argument boundaries.
            return $"\"{executablePath}\" --open-url \"%1\"";
        }
    }
}
