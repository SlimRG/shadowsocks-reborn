using System;
using System.ComponentModel;
using Shadowsocks.Engine;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Util.SystemProxy;

namespace Shadowsocks.Controller
{
    public enum SystemProxyMode
    {
        Disabled,
        Global,
        Pac,
    }

    public static class SystemProxy
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        internal static SystemProxyMode GetCurrentMode(Configuration config, PACServer pacSrv)
        {
            if (!config.enabled || !WinINet.Operational)
            {
                return SystemProxyMode.Disabled;
            }

            try
            {
                WinINetSetting setting = WinINet.Query();
                if (config.global)
                {
                    string expectedServer = $"localhost:{config.localPort}";
                    return setting.Flags.HasFlag(InternetPerConnectionFlags.Proxy) &&
                           string.Equals(setting.ProxyServer, expectedServer, StringComparison.OrdinalIgnoreCase)
                        ? SystemProxyMode.Global
                        : SystemProxyMode.Disabled;
                }

                string expectedPacUrl = pacSrv?.PacUrl;

                return !string.IsNullOrEmpty(expectedPacUrl) &&
                       setting.Flags.HasFlag(InternetPerConnectionFlags.AutoProxyUrl) &&
                       string.Equals(setting.AutoConfigUrl, expectedPacUrl, StringComparison.OrdinalIgnoreCase)
                    ? SystemProxyMode.Pac
                    : SystemProxyMode.Disabled;
            }
            catch (Win32Exception ex)
            {
                logger.LogUsefulException(ex);
                return SystemProxyMode.Disabled;
            }
        }

        public static void Update(Configuration config, bool forceDisable, PACServer pacSrv, IUserInteractionService interaction = null, bool noRetry = false)
        {
            interaction ??= NullUserInteractionService.Instance;
            bool enabled = config.enabled && !forceDisable;

            try
            {
                if (!WinINet.Operational)
                {
                    throw new InvalidOperationException("WinINet system proxy integration is unavailable.");
                }

                if (!enabled)
                {
                    WinINet.Restore();
                    return;
                }

                if (config.global)
                {
                    WinINet.ProxyGlobal($"localhost:{config.localPort}");
                    return;
                }

                string pacUrl = pacSrv?.PacUrl ?? throw new InvalidOperationException("PAC server is not initialized.");

                // Both Local PAC and Online PAC are served from localhost. For Online PAC
                // this points at the persistent cache maintained by OnlinePacCache, so
                // WinINet never needs direct access to the remote PAC URL.
                WinINet.ProxyPAC(pacUrl);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                logger.LogUsefulException(ex);
                if (!noRetry)
                {
                    bool retry = interaction.Confirm(
                        I18N.GetString("Error occured when process proxy setting, do you want reset current setting and retry?"),
                        I18N.GetString("shadowsocks-reborn"));
                    if (retry)
                    {
                        try
                        {
                            WinINet.Reset();
                            Update(config, forceDisable, pacSrv, interaction, true);
                            return;
                        }
                        catch (Exception retryException)
                        {
                            logger.LogUsefulException(retryException);
                        }
                    }
                }

                interaction.ShowError(
                    I18N.GetString("Unrecoverable proxy setting error occured, see log for detail"),
                    I18N.GetString("shadowsocks-reborn"));
            }
        }
    }
}
