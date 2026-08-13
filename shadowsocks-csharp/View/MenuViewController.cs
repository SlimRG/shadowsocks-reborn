using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Localization;
using Shadowsocks.Model;
using Shadowsocks.Properties;
using Shadowsocks.Util;
using Shadowsocks.Views;

namespace Shadowsocks.View
{
    public class MenuViewController
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private ShadowsocksController controller;
        public UpdateChecker updateChecker;

        private NotifyIcon _notifyIcon;
        private Icon icon, icon_in, icon_out, icon_both, previousIcon;

        private bool _isStartupCheck;
        private string _urlToOpen;

        private ContextMenuStrip contextMenu1;
        private ToolStripMenuItem disableItem;
        private ToolStripMenuItem AutoStartupItem;
        private ToolStripMenuItem ProtocolHandlerItem;
        private ToolStripMenuItem ShareOverLANItem;
        private ToolStripSeparator SeparatorItem;
        private ToolStripMenuItem ConfigItem;
        private ToolStripMenuItem ServersItem;
        private ToolStripMenuItem globalModeItem;
        private ToolStripMenuItem PACModeItem;
        private ToolStripMenuItem localPACItem;
        private ToolStripMenuItem onlinePACItem;
        private ToolStripMenuItem editLocalPACItem;
        private ToolStripMenuItem updateFromGeositeItem;
        private ToolStripMenuItem geositeSourcesItem;
        private ToolStripMenuItem editGFWUserRuleItem;
        private ToolStripMenuItem editOnlinePACItem;
        private ToolStripMenuItem secureLocalPacUrlToggleItem;
        private ToolStripMenuItem regenerateLocalPacOnUpdateItem;
        private ToolStripMenuItem autoCheckUpdatesToggleItem;
        private ToolStripMenuItem checkPreReleaseToggleItem;
        private ToolStripMenuItem proxyItem;
        private ToolStripMenuItem hotKeyItem;
        private ToolStripMenuItem VerboseLoggingToggleItem;
        private ToolStripMenuItem ShowPluginOutputToggleItem;
        private ToolStripMenuItem WriteI18NFileItem;
        private ToolStripMenuItem onlineConfigItem;

        private ConfigForm configForm;
        private LogForm logForm;

        private System.Windows.Window serverSharingWindow;
        private System.Windows.Window hotkeysWindow;
        private System.Windows.Window forwardProxyWindow;
        private System.Windows.Window onlineConfigWindow;
        private System.Windows.Window geositeSourcesWindow;

        // color definition for icon color transformation
        private readonly Color colorMaskBlue = Color.FromArgb(255, 25, 125, 191);
        private readonly Color colorMaskDarkSilver = Color.FromArgb(128, 192, 192, 192);
        private readonly Color colorMaskLightSilver = Color.FromArgb(192, 192, 192);
        private readonly Color colorMaskEclipse = Color.FromArgb(192, 64, 64, 64);

        public MenuViewController(ShadowsocksController controller)
        {
            this.controller = controller;

            LoadMenu();

            controller.EnableStatusChanged += controller_EnableStatusChanged;
            controller.ConfigChanged += controller_ConfigChanged;
            controller.PACFileReadyToOpen += controller_FileReadyToOpen;
            controller.UserRuleFileReadyToOpen += controller_FileReadyToOpen;
            controller.ShareOverLANStatusChanged += controller_ShareOverLANStatusChanged;
            controller.VerboseLoggingStatusChanged += controller_VerboseLoggingStatusChanged;
            controller.ShowPluginOutputChanged += controller_ShowPluginOutputChanged;
            controller.EnableGlobalChanged += controller_EnableGlobalChanged;
            controller.Errored += controller_Errored;
            controller.UpdatePACFromGeositeCompleted += controller_UpdatePACFromGeositeCompleted;
            controller.UpdatePACFromGeositeError += controller_UpdatePACFromGeositeError;

            _notifyIcon = new NotifyIcon();
            UpdateTrayIconAndNotifyText();
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenuStrip = contextMenu1;
            _notifyIcon.BalloonTipClicked += notifyIcon1_BalloonTipClicked;
            _notifyIcon.MouseClick += notifyIcon1_Click;
            _notifyIcon.MouseDoubleClick += notifyIcon1_DoubleClick;
            _notifyIcon.BalloonTipClosed += _notifyIcon_BalloonTipClosed;
            controller.TrafficChanged += controller_TrafficChanged;

            updateChecker = new UpdateChecker();
            updateChecker.CheckUpdateCompleted += updateChecker_CheckUpdateCompleted;

            LoadCurrentConfiguration();

            Configuration config = controller.GetCurrentConfiguration();

            if (config.firstRun)
            {
                ShowConfigForm();
            }
            else if (config.autoCheckUpdate)
            {
                _isStartupCheck = true;
                Dispatcher.CurrentDispatcher.Invoke(() => updateChecker.CheckForVersionUpdate(3000));
            }
        }

        #region Tray Icon

        private void UpdateTrayIconAndNotifyText()
        {
            Configuration config = controller.GetCurrentConfiguration();
            SystemProxyMode proxyMode = controller.GetSystemProxyMode();
            bool enabled = proxyMode != SystemProxyMode.Disabled;
            bool global = proxyMode == SystemProxyMode.Global;

            Color colorMask = SelectColorMask(enabled, global);
            Size iconSize = SelectIconSize();

            UpdateIconSet(colorMask, iconSize, out icon, out icon_in, out icon_out, out icon_both);

            previousIcon = icon;
            _notifyIcon.Icon = previousIcon;

            string serverInfo = null;
            if (controller.GetCurrentStrategy() != null)
            {
                serverInfo = controller.GetCurrentStrategy().Name;
            }
            else
            {
                serverInfo = config.GetCurrentServer().ToString();
            }
            // show more info by hacking the P/Invoke declaration for NOTIFYICONDATA inside Windows Forms
            string text = I18N.GetString("Shadowsocks") + " " + UpdateChecker.Version + "\n" +
                          (enabled ?
                              I18N.GetString("System Proxy On:") + " " + (global ? I18N.GetString("Global") : I18N.GetString("PAC")) :
                              I18N.GetString("Running: Port {0}", config.localPort))  // this feedback is very important because they need to know Shadowsocks is running
                          + "\n" + serverInfo;
            if (text.Length > 127)
            {
                text = text.Substring(0, 126 - 3) + "...";
            }
            ViewUtils.SetNotifyIconText(_notifyIcon, text);
        }

        /// <summary>
        /// Determine the icon size based on the screen DPI.
        /// </summary>
        /// <returns></returns>
        /// https://stackoverflow.com/a/40851713/2075611
        private Size SelectIconSize()
        {
            Size size = new Size(32, 32);
            int dpi = ViewUtils.GetScreenDpi();
            if (dpi < 97)
            {
                // dpi = 96;
                size = new Size(16, 16);
            }
            else if (dpi < 121)
            {
                // dpi = 120;
                size = new Size(20, 20);
            }
            else if (dpi < 145)
            {
                // dpi = 144;
                size = new Size(24, 24);
            }
            else
            {
                // dpi = 168;
                size = new Size(28, 28);
            }
            return size;
        }

        private Color SelectColorMask(bool isProxyEnabled, bool isGlobalProxy)
        {
            Color colorMask = Color.White;

            Utils.WindowsThemeMode currentWindowsThemeMode = Utils.GetWindows10SystemThemeSetting();

            if (isProxyEnabled)
            {
                if (isGlobalProxy)  // global
                {
                    colorMask = colorMaskBlue;
                }
                else  // PAC
                {
                    if (currentWindowsThemeMode == Utils.WindowsThemeMode.Light)
                    {
                        colorMask = colorMaskEclipse;
                    }
                }
            }
            else  // disabled
            {
                if (currentWindowsThemeMode == Utils.WindowsThemeMode.Light)
                {
                    colorMask = colorMaskDarkSilver;
                }
                else
                {
                    colorMask = colorMaskLightSilver;
                }
            }

            return colorMask;
        }

        private void UpdateIconSet(Color colorMask, Size size,
            out Icon icon, out Icon icon_in, out Icon icon_out, out Icon icon_both)
        {
            Bitmap iconBitmap;

            // generate the base icon
            iconBitmap = ViewUtils.ChangeBitmapColor(Resources.ss32Fill, colorMask);
            iconBitmap = ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32Outline);

            icon = Icon.FromHandle(ViewUtils.ResizeBitmap(iconBitmap, size.Width, size.Height).GetHicon());
            icon_in = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In), size.Width, size.Height).GetHicon());
            icon_out = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In), size.Width, size.Height).GetHicon());
            icon_both = Icon.FromHandle(ViewUtils.ResizeBitmap(ViewUtils.AddBitmapOverlay(iconBitmap, Resources.ss32In, Resources.ss32Out), size.Width, size.Height).GetHicon());
        }

        #endregion

        #region MenuItems and MenuGroups

        private ToolStripMenuItem CreateMenuItem(string text, EventHandler click)
        {
            return new ToolStripMenuItem(I18N.GetString(text), null, click);
        }

        private ToolStripMenuItem CreateMenuGroup(string text, ToolStripItem[] items)
        {
            return new ToolStripMenuItem(I18N.GetString(text), null, items);
        }

        private void LoadMenu()
        {
            contextMenu1 = new ContextMenuStrip();
            contextMenu1.Items.AddRange(new ToolStripItem[] {
                CreateMenuGroup("System Proxy", new ToolStripItem[] {
                    disableItem = CreateMenuItem("Disable", EnableItem_Click),
                    PACModeItem = CreateMenuItem("PAC", PACModeItem_Click),
                    globalModeItem = CreateMenuItem("Global", GlobalModeItem_Click)
                }),
                ServersItem = CreateMenuGroup("Servers", new ToolStripItem[] {
                    SeparatorItem = new ToolStripSeparator(),
                    ConfigItem = CreateMenuItem("Edit Servers...", Config_Click),
                    new ToolStripSeparator(),
                    CreateMenuItem("Share Server Config...", QRCodeItem_Click),
                    CreateMenuItem("Scan QRCode from Screen...", ScanQRCodeItem_Click),
                    CreateMenuItem("Import URL from Clipboard...", ImportURLItem_Click)
                }),
                CreateMenuGroup("PAC ", new ToolStripItem[] {
                    localPACItem = CreateMenuItem("Local PAC", LocalPACItem_Click),
                    onlinePACItem = CreateMenuItem("Online PAC", OnlinePACItem_Click),
                    new ToolStripSeparator(),
                    editLocalPACItem = CreateMenuItem("Edit Local PAC File...", EditPACFileItem_Click),
                    updateFromGeositeItem = CreateMenuItem("Update Local PAC from Geosite", UpdatePACFromGeositeItem_Click),
                    geositeSourcesItem = CreateMenuItem("GeoSite Sources...", GeositeSourcesItem_Click),
                    editGFWUserRuleItem = CreateMenuItem("Edit User Rule for Geosite...", EditUserRuleFileForGeositeItem_Click),
                    secureLocalPacUrlToggleItem = CreateMenuItem("Secure Local PAC", SecureLocalPacUrlToggleItem_Click),
                    regenerateLocalPacOnUpdateItem = CreateMenuItem("Regenerate local PAC on version update", RegenerateLocalPacOnUpdateItem_Click),
                    CreateMenuItem("Copy Local PAC URL", CopyLocalPacUrlItem_Click),
                    editOnlinePACItem = CreateMenuItem("Edit Online PAC URL...", UpdateOnlinePACURLItem_Click),
                }),
                proxyItem = CreateMenuItem("Forward Proxy...", proxyItem_Click),
                onlineConfigItem = CreateMenuItem("Online Config...", OnlineConfig_Click),
                new ToolStripSeparator(),
                AutoStartupItem = CreateMenuItem("Start on Boot", AutoStartupItem_Click),
                ProtocolHandlerItem = CreateMenuItem("Associate ss:// Links", ProtocolHandlerItem_Click),
                ShareOverLANItem = CreateMenuItem("Allow other Devices to connect", ShareOverLANItem_Click),
                new ToolStripSeparator(),
                hotKeyItem = CreateMenuItem("Edit Hotkeys...", hotKeyItem_Click),
                CreateMenuGroup("Help", new ToolStripItem[] {
                    CreateMenuItem("Show Logs...", ShowLogItem_Click),
                    VerboseLoggingToggleItem = CreateMenuItem("Verbose Logging", VerboseLoggingToggleItem_Click),
                    ShowPluginOutputToggleItem = CreateMenuItem("Show Plugin Output", ShowPluginOutputToggleItem_Click),
                    WriteI18NFileItem = CreateMenuItem("Write translation template", WriteI18NFileItem_Click),
                    CreateMenuGroup("Updates...", new ToolStripItem[] {
                        CreateMenuItem("Check for Updates...", checkUpdatesItem_Click),
                        new ToolStripSeparator(),
                        autoCheckUpdatesToggleItem = CreateMenuItem("Check for Updates at Startup", autoCheckUpdatesToggleItem_Click),
                        checkPreReleaseToggleItem = CreateMenuItem("Check Pre-release Version", checkPreReleaseToggleItem_Click),
                    }),
                    CreateMenuItem("About...", AboutItem_Click),
                }),
                new ToolStripSeparator(),
                CreateMenuItem("Quit", Quit_Click)
            });
        }

        #endregion

        private void controller_TrafficChanged(object sender, EventArgs e)
        {
            if (icon == null)
                return;

            Icon newIcon;

            bool hasInbound = controller.trafficPerSecondQueue.Last().inboundIncreasement > 0;
            bool hasOutbound = controller.trafficPerSecondQueue.Last().outboundIncreasement > 0;

            if (hasInbound && hasOutbound)
                newIcon = icon_both;
            else if (hasInbound)
                newIcon = icon_in;
            else if (hasOutbound)
                newIcon = icon_out;
            else
                newIcon = icon;

            if (newIcon != this.previousIcon)
            {
                this.previousIcon = newIcon;
                _notifyIcon.Icon = newIcon;
            }
        }

        void controller_Errored(object sender, ErrorEventArgs e)
        {
            MessageBox.Show(e.GetException().ToString(), I18N.GetString("Shadowsocks Error: {0}", e.GetException().Message));
        }

        private void controller_ConfigChanged(object sender, EventArgs e)
        {
            LoadCurrentConfiguration();
            UpdateTrayIconAndNotifyText();
        }

        private void LoadCurrentConfiguration()
        {
            Configuration config = controller.GetCurrentConfiguration();
            UpdateServersMenu();
            UpdateSystemProxyItemsEnabledStatus();
            ShareOverLANItem.Checked = config.shareOverLan;
            VerboseLoggingToggleItem.Checked = config.isVerboseLogging;
            ShowPluginOutputToggleItem.Checked = config.showPluginOutput;
            AutoStartupItem.Checked = AutoStartup.Check();
            ProtocolHandlerItem.Checked = ProtocolHandler.Check();
            onlinePACItem.Checked = onlinePACItem.Enabled && config.useOnlinePac;
            localPACItem.Checked = !onlinePACItem.Checked;
            secureLocalPacUrlToggleItem.Checked = config.secureLocalPac;
            regenerateLocalPacOnUpdateItem.Checked = config.regeneratePacOnUpdate;
            UpdatePACItemsEnabledStatus();
            UpdateUpdateMenu();
        }

        #region Forms

        private void ShowConfigForm()
        {
            if (configForm != null)
            {
                configForm.Activate();
            }
            else
            {
                configForm = new ConfigForm(controller);
                configForm.Show();
                configForm.Activate();
                configForm.FormClosed += configForm_FormClosed;
            }
        }

        private void ShowLogForm()
        {
            if (logForm != null)
            {
                logForm.Activate();
            }
            else
            {
                logForm = new LogForm(controller);
                logForm.Show();
                logForm.Activate();
                logForm.FormClosed += logForm_FormClosed;
            }
        }

        void logForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            logForm.Dispose();
            logForm = null;
        }

        void configForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            configForm.Dispose();
            configForm = null;
            var config = controller.GetCurrentConfiguration();
            if (config.firstRun)
            {
                CheckUpdateForFirstRun();
                ShowBalloonTip(
                    I18N.GetString("Shadowsocks is here"),
                    I18N.GetString("You can turn on/off Shadowsocks in the context menu"),
                    ToolTipIcon.Info,
                    0
                );
                config.firstRun = false;
            }
        }

        #endregion

        #region Misc

        void ShowBalloonTip(string title, string content, ToolTipIcon icon, int timeout)
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = content;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(timeout);
        }

        void notifyIcon1_BalloonTipClicked(object sender, EventArgs e)
        {
        }

        private void _notifyIcon_BalloonTipClosed(object sender, EventArgs e)
        {
        }

        private void notifyIcon1_Click(object sender, MouseEventArgs e)
        {
            UpdateTrayIconAndNotifyText();
            if (e.Button == MouseButtons.Middle)
            {
                ShowLogForm();
            }
        }

        private void notifyIcon1_DoubleClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowConfigForm();
            }
        }

        private void CheckUpdateForFirstRun()
        {
            Configuration config = controller.GetCurrentConfiguration();
            if (config.firstRun)
                return;
            _isStartupCheck = true;
            Dispatcher.CurrentDispatcher.Invoke(() => updateChecker.CheckForVersionUpdate(3000));
        }

        public void ShowLogForm_HotKey()
        {
            ShowLogForm();
        }

        #endregion

        #region Main menu

        void controller_ShareOverLANStatusChanged(object sender, EventArgs e)
        {
            ShareOverLANItem.Checked = controller.GetCurrentConfiguration().shareOverLan;
        }

        private void proxyItem_Click(object sender, EventArgs e)
        {
            if (forwardProxyWindow == null)
            {
                forwardProxyWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("ForwardProxy"),
                    Height = 400,
                    Width = 280,
                    MinHeight = 400,
                    MinWidth = 280,
                    Content = new ForwardProxyView()
                };
                forwardProxyWindow.Closed += ForwardProxyWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(forwardProxyWindow);
                forwardProxyWindow.Show();
            }
            forwardProxyWindow.Activate();
        }

        private void ForwardProxyWindow_Closed(object sender, EventArgs e)
        {
            forwardProxyWindow = null;
        }

        public void CloseForwardProxyWindow() => forwardProxyWindow.Close();

        private void OnlineConfig_Click(object sender, EventArgs e)
        {
            if (onlineConfigWindow == null)
            {
                onlineConfigWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("OnlineConfigDelivery"),
                    Height = 510,
                    Width = 480,
                    MinHeight = 510,
                    MinWidth = 480,
                    Content = new OnlineConfigView()
                };
                onlineConfigWindow.Closed += OnlineConfigWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(onlineConfigWindow);
                onlineConfigWindow.Show();
            }
            onlineConfigWindow.Activate();
        }

        private void OnlineConfigWindow_Closed(object sender, EventArgs e)
        {
            onlineConfigWindow = null;
        }

        private void GeositeSourcesItem_Click(object sender, EventArgs e)
        {
            if (geositeSourcesWindow == null)
            {
                geositeSourcesWindow = new System.Windows.Window()
                {
                    Title = I18N.GetString("GeoSite Sources"),
                    Height = 500,
                    Width = 740,
                    MinHeight = 440,
                    MinWidth = 620,
                    Content = new GeositeSourcesView()
                };
                geositeSourcesWindow.Closed += GeositeSourcesWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(geositeSourcesWindow);
                geositeSourcesWindow.Show();
            }
            geositeSourcesWindow.Activate();
        }

        private void GeositeSourcesWindow_Closed(object sender, EventArgs e)
        {
            geositeSourcesWindow = null;
        }

        public void CloseGeositeSourcesWindow()
        {
            geositeSourcesWindow?.Close();
        }

        private void hotKeyItem_Click(object sender, EventArgs e)
        {
            if (hotkeysWindow == null)
            {
                hotkeysWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("Hotkeys"),
                    Height = 260,
                    Width = 320,
                    MinHeight = 260,
                    MinWidth = 320,
                    Content = new HotkeysView()
                };
                hotkeysWindow.Closed += HotkeysWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(hotkeysWindow);
                hotkeysWindow.Show();
            }
            hotkeysWindow.Activate();
        }

        private void HotkeysWindow_Closed(object sender, EventArgs e)
        {
            hotkeysWindow = null;
        }

        public void CloseHotkeysWindow() => hotkeysWindow.Close();

        private void ShareOverLANItem_Click(object sender, EventArgs e)
        {
            ShareOverLANItem.Checked = !ShareOverLANItem.Checked;
            controller.ToggleShareOverLAN(ShareOverLANItem.Checked);
        }

        private void AutoStartupItem_Click(object sender, EventArgs e)
        {
            AutoStartupItem.Checked = !AutoStartupItem.Checked;
            if (!AutoStartup.Set(AutoStartupItem.Checked))
            {
                MessageBox.Show(I18N.GetString("Failed to update registry"));
            }
            LoadCurrentConfiguration();
        }

        private void ProtocolHandlerItem_Click(object sender, EventArgs e)
        {
            ProtocolHandlerItem.Checked = !ProtocolHandlerItem.Checked;
            if (!ProtocolHandler.Set(ProtocolHandlerItem.Checked))
            {
                MessageBox.Show(I18N.GetString("Failed to update registry"));
            }
            LoadCurrentConfiguration();
        }

        private void Quit_Click(object sender, EventArgs e)
        {
            controller.Stop();
            _notifyIcon.Visible = false;
            Application.Exit();
        }

        #endregion

        #region System proxy

        private void controller_EnableStatusChanged(object sender, EventArgs e)
        {
            UpdateSystemProxyItemsEnabledStatus();
        }

        private void EnableItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(false);
            UpdateSystemProxyItemsEnabledStatus();
        }

        void controller_EnableGlobalChanged(object sender, EventArgs e)
        {
            UpdateSystemProxyItemsEnabledStatus();
        }

        private void UpdateSystemProxyItemsEnabledStatus()
        {
            SystemProxyMode mode = controller.GetSystemProxyMode();
            disableItem.Checked = mode == SystemProxyMode.Disabled;
            globalModeItem.Checked = mode == SystemProxyMode.Global;
            PACModeItem.Checked = mode == SystemProxyMode.Pac;
        }

        private void GlobalModeItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(true);
            controller.ToggleGlobal(true);
            UpdateSystemProxyItemsEnabledStatus();
        }

        private void PACModeItem_Click(object sender, EventArgs e)
        {
            controller.ToggleEnable(true);
            controller.ToggleGlobal(false);
            UpdateSystemProxyItemsEnabledStatus();
        }

        #endregion

        #region Server

        private void UpdateServersMenu()
        {
            var items = ServersItem.DropDownItems;
            while (items[0] != SeparatorItem)
            {
                items.RemoveAt(0);
            }
            int strategyCount = 0;
            foreach (var strategy in controller.GetStrategies())
            {
                ToolStripMenuItem item = new ToolStripMenuItem(strategy.Name);
                item.Tag = strategy.ID;
                item.Click += AStrategyItem_Click;
                items.Insert(strategyCount, item);
                strategyCount++;
            }

            // user wants a separator item between strategy and servers menu group
            items.Insert(strategyCount++, new ToolStripSeparator());

            int maxCount = 20;
            int serverCount = 0;
            bool overflow = false;
            bool needAdd = true;

            Configuration configuration = controller.GetCurrentConfiguration();
            for (int i = 0; i < configuration.configs.Count; i++)
            {
                try
                {
                    if (overflow)
                    {
                        needAdd = configuration.index >= i;
                        if (needAdd)
                        {
                            i = configuration.index;
                        }
                    }

                    if (needAdd)
                    {
                        var server = configuration.configs[i];
                        Configuration.CheckServer(server);
                        var item = new ToolStripMenuItem(server.ToString());
                        item.Tag = i;
                        item.Click += AServerItem_Click;
                        items.Insert(strategyCount + serverCount, item);
                        serverCount++;
                    }

                    if (overflow)
                    {
                        items.Insert(strategyCount + serverCount,
                            new ToolStripMenuItem(I18N.GetString("... more than {0} (total {1})", maxCount, configuration.configs.Count), null, Config_Click));
                        break;
                    }

                    overflow = serverCount > maxCount;
                }
                catch
                {
                }
            }

            foreach (ToolStripItem stripItem in items)
            {
                if (stripItem is not ToolStripMenuItem item || item.Tag == null)
                    continue;

                item.Checked = item.Tag.ToString() == configuration.index.ToString()
                    || item.Tag.ToString() == configuration.strategy;
            }
        }

        private void AServerItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            controller.SelectServerIndex((int)item.Tag);
        }

        private void AStrategyItem_Click(object sender, EventArgs e)
        {
            ToolStripMenuItem item = (ToolStripMenuItem)sender;
            controller.SelectStrategy((string)item.Tag);
        }

        private void Config_Click(object sender, EventArgs e)
        {
            ShowConfigForm();
        }

        private void OpenUrlFromQrCode()
        {
            Process.Start(new ProcessStartInfo(_urlToOpen) { UseShellExecute = true });
        }

        private void QRCodeItem_Click(object sender, EventArgs e)
        {
            if (serverSharingWindow == null)
            {
                serverSharingWindow = new System.Windows.Window()
                {
                    Title = LocalizationProvider.GetLocalizedValue<string>("ServerSharing"),
                    Height = 400,
                    Width = 660,
                    MinHeight = 400,
                    MinWidth = 660,
                    Content = new ServerSharingView()
                };
                serverSharingWindow.Closed += ServerSharingWindow_Closed;
                ElementHost.EnableModelessKeyboardInterop(serverSharingWindow);
                serverSharingWindow.Show();
            }
            serverSharingWindow.Activate();
        }

        private void ServerSharingWindow_Closed(object sender, EventArgs e)
        {
            serverSharingWindow = null;
        }

        private void ScanQRCodeItem_Click(object sender, EventArgs e)
        {
            var result = Utils.ScanQRCodeFromScreen();
            if (result != null)
            {
                if (result.ToLowerInvariant().StartsWith("http://") || result.ToLowerInvariant().StartsWith("https://"))
                {
                    _urlToOpen = result;
                    OpenUrlFromQrCode();
                }
                else if (controller.AddServerBySSURL(result))
                {
                    ShowConfigForm();
                }
                else
                {
                    MessageBox.Show(I18N.GetString("Invalid QR Code content: {0}", result));
                }
                return;
            }
            else
                MessageBox.Show(I18N.GetString("No QRCode found. Try to zoom in or move it to the center of the screen."));
        }

        private void ImportURLItem_Click(object sender, EventArgs e)
        {
            if (controller.AskAddServerBySSURL(Clipboard.GetText(TextDataFormat.Text)))
            {
                ShowConfigForm();
            }
        }

        #endregion

        #region PAC

        private void LocalPACItem_Click(object sender, EventArgs e)
        {
            if (!localPACItem.Checked)
            {
                localPACItem.Checked = true;
                onlinePACItem.Checked = false;
                controller.UseOnlinePAC(false);
                UpdatePACItemsEnabledStatus();
            }
        }

        private void OnlinePACItem_Click(object sender, EventArgs e)
        {
            if (!onlinePACItem.Checked)
            {
                if (string.IsNullOrEmpty(controller.GetCurrentConfiguration().pacUrl))
                {
                    UpdateOnlinePACURLItem_Click(sender, e);
                }
                if (!string.IsNullOrEmpty(controller.GetCurrentConfiguration().pacUrl))
                {
                    localPACItem.Checked = false;
                    onlinePACItem.Checked = true;
                    controller.UseOnlinePAC(true);
                }
                UpdatePACItemsEnabledStatus();
            }
        }

        private void UpdateOnlinePACURLItem_Click(object sender, EventArgs e)
        {
            string origPacUrl = controller.GetCurrentConfiguration().pacUrl;
            string pacUrl = Microsoft.VisualBasic.Interaction.InputBox(
                I18N.GetString("Please input PAC Url"),
                I18N.GetString("Edit Online PAC URL"),
                origPacUrl, -1, -1);
            if (!string.IsNullOrEmpty(pacUrl) && pacUrl != origPacUrl)
            {
                controller.SavePACUrl(pacUrl);
            }
        }

        private void SecureLocalPacUrlToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleSecureLocalPac(!configuration.secureLocalPac);
        }

        private void RegenerateLocalPacOnUpdateItem_Click(object sender, EventArgs e)
        {
            var config = controller.GetCurrentConfiguration();
            controller.ToggleRegeneratePacOnUpdate(!config.regeneratePacOnUpdate);
        }

        private void CopyLocalPacUrlItem_Click(object sender, EventArgs e)
        {
            controller.CopyPacUrl();
        }

        private void UpdatePACItemsEnabledStatus()
        {
            if (this.localPACItem.Checked)
            {
                this.editLocalPACItem.Enabled = true;
                this.updateFromGeositeItem.Enabled = true;
                this.geositeSourcesItem.Enabled = true;
                this.editGFWUserRuleItem.Enabled = true;
                this.editOnlinePACItem.Enabled = false;
            }
            else
            {
                this.editLocalPACItem.Enabled = false;
                this.updateFromGeositeItem.Enabled = false;
                this.geositeSourcesItem.Enabled = false;
                this.editGFWUserRuleItem.Enabled = false;
                this.editOnlinePACItem.Enabled = true;
            }
        }

        private void EditPACFileItem_Click(object sender, EventArgs e)
        {
            controller.TouchPACFile();
        }

        private async void UpdatePACFromGeositeItem_Click(object sender, EventArgs e)
        {
            await GeositeUpdater.UpdatePACFromGeosite();
        }

        private void EditUserRuleFileForGeositeItem_Click(object sender, EventArgs e)
        {
            controller.TouchUserRuleFile();
        }

        void controller_FileReadyToOpen(object sender, ShadowsocksController.PathEventArgs e)
        {
            string argument = @"/select, " + e.Path;

            Process.Start("explorer.exe", argument);
        }

        void controller_UpdatePACFromGeositeError(object sender, System.IO.ErrorEventArgs e)
        {
            ShowBalloonTip(I18N.GetString("Failed to update PAC file"), e.GetException().Message, ToolTipIcon.Error, 5000);
            logger.LogUsefulException(e.GetException());
        }

        void controller_UpdatePACFromGeositeCompleted(object sender, GeositeResultEventArgs e)
        {
            string result = e.Success
                ? I18N.GetString("PAC updated")
                : I18N.GetString("No updates found. Please report to Geosite if you have problems with it.");
            ShowBalloonTip(I18N.GetString("Shadowsocks"), result, ToolTipIcon.Info, 1000);
        }

        #endregion

        #region Help

        void controller_VerboseLoggingStatusChanged(object sender, EventArgs e)
        {
            VerboseLoggingToggleItem.Checked = controller.GetCurrentConfiguration().isVerboseLogging;
        }

        void controller_ShowPluginOutputChanged(object sender, EventArgs e)
        {
            ShowPluginOutputToggleItem.Checked = controller.GetCurrentConfiguration().showPluginOutput;
        }

        private void VerboseLoggingToggleItem_Click(object sender, EventArgs e)
        {
            VerboseLoggingToggleItem.Checked = !VerboseLoggingToggleItem.Checked;
            controller.ToggleVerboseLogging(VerboseLoggingToggleItem.Checked);
        }

        private void ShowLogItem_Click(object sender, EventArgs e)
        {
            ShowLogForm();
        }

        private void ShowPluginOutputToggleItem_Click(object sender, EventArgs e)
        {
            ShowPluginOutputToggleItem.Checked = !ShowPluginOutputToggleItem.Checked;
            controller.ToggleShowPluginOutput(ShowPluginOutputToggleItem.Checked);
        }

        private void WriteI18NFileItem_Click(object sender, EventArgs e)
        {
            File.WriteAllText(I18N.I18N_FILE, Resources.i18n_csv, Encoding.UTF8);
        }

        #endregion

        #region Update

        void updateChecker_CheckUpdateCompleted(object sender, EventArgs e)
        {
            if (!_isStartupCheck && updateChecker.NewReleaseZipFilename == null)
            {
                ShowBalloonTip(I18N.GetString("Shadowsocks"), I18N.GetString("No update is available"), ToolTipIcon.Info, 5000);
            }
            _isStartupCheck = false;
        }

        private void UpdateUpdateMenu()
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            autoCheckUpdatesToggleItem.Checked = configuration.autoCheckUpdate;
            checkPreReleaseToggleItem.Checked = configuration.checkPreRelease;
        }

        private void autoCheckUpdatesToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleCheckingUpdate(!configuration.autoCheckUpdate);
            UpdateUpdateMenu();
        }

        private void checkPreReleaseToggleItem_Click(object sender, EventArgs e)
        {
            Configuration configuration = controller.GetCurrentConfiguration();
            controller.ToggleCheckingPreRelease(!configuration.checkPreRelease);
            UpdateUpdateMenu();
        }

        private async void checkUpdatesItem_Click(object sender, EventArgs e)
        {
            await updateChecker.CheckForVersionUpdate();
        }

        private void AboutItem_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://github.com/shadowsocks/shadowsocks-windows") { UseShellExecute = true });
        }

        #endregion
    }
}
