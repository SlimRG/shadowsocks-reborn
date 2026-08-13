using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CommandLine;
using Microsoft.Win32;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Hotkeys;
using Shadowsocks.Util;
using Shadowsocks.View;
using WPFLocalizeExtension.Engine;

namespace Shadowsocks
{
    internal static class Program
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static ShadowsocksController MainController { get; private set; }
        public static MenuViewController MenuController { get; private set; }
        public static CommandLineOption Options { get; private set; }
        public static string[] Args { get; private set; }

        // https://github.com/dotnet/runtime/issues/13051#issuecomment-510267727
        public static readonly string ExecutablePath = Process.GetCurrentProcess().MainModule?.FileName
            ?? Application.ExecutablePath;
        public static readonly string WorkingDirectory = Path.GetDirectoryName(ExecutablePath)
            ?? AppContext.BaseDirectory;

        private static readonly Mutex mutex = new Mutex(true, $"Shadowsocks_{Utils.GetDeterministicHashCode(ExecutablePath)}");

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        private static void Main(string[] args)
        {
            #region Single instance and IPC
            bool hasAnotherInstance = !mutex.WaitOne(TimeSpan.Zero, true);

            // Store arguments for later use.
            Args = args;
            Parser.Default.ParseArguments<CommandLineOption>(args)
                .WithParsed(opt => Options = opt)
                .WithNotParsed(e => e.Output());

            if (hasAnotherInstance)
            {
                if (!string.IsNullOrWhiteSpace(Options.OpenUrl))
                {
                    IPCService.RequestOpenUrl(Options.OpenUrl);
                }
                else
                {
                    MessageBox.Show(I18N.GetString("Find Shadowsocks icon in your notify tray.")
                                    + Environment.NewLine
                                    + I18N.GetString("If you want to start multiple Shadowsocks, make a copy in another directory."),
                        I18N.GetString("Shadowsocks is already running."));
                }
                return;
            }
            #endregion

            #region Environment setup
            Directory.SetCurrentDirectory(WorkingDirectory);
            Model.NLogConfig.TouchAndApplyNLogConfig();

            #endregion

            #region Compatibility check
            // Check the OS because the client uses dual-mode sockets.
            if (!Utils.IsWinVistaOrHigher())
            {
                MessageBox.Show(I18N.GetString("Unsupported operating system, use Windows Vista at least."),
                "Shadowsocks Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            #endregion

            #region Event handlers setup
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            // Handle UI exceptions.
            Application.ThreadException += Application_ThreadException;
            // Handle non-UI exceptions.
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            Application.ApplicationExit += Application_ApplicationExit;
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AutoStartup.RegisterForRestart(true);
            #endregion

            // Workaround for hosting WPF controls in a WinForms app.
            // We have to manually set the culture for the LocalizeDictionary instance.
            // https://stackoverflow.com/questions/374518/localizing-a-winforms-application-with-embedded-wpf-user-controls
            // https://stackoverflow.com/questions/14668640/wpf-localize-extension-translate-window-at-run-time
            LocalizeDictionary.Instance.Culture = Thread.CurrentThread.CurrentCulture;

#if DEBUG
            // truncate privoxy log file while debugging
            string privoxyLogFilename = Utils.GetTempPath("privoxy.log");
            if (File.Exists(privoxyLogFilename))
                using (new FileStream(privoxyLogFilename, FileMode.Truncate)) { }
#endif
            MainController = new ShadowsocksController();
            MenuController = new MenuViewController(MainController);

            HotKeys.Init(MainController);
            MainController.Start();

            // Update online configuration after startup.
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                await MainController.UpdateAllOnlineConfig();
            });

            #region IPC handler and argument processing
            IPCService ipcService = new IPCService();
            Task.Run(() => ipcService.RunServer());
            ipcService.OpenUrlRequested += (_, e) => MainController.AskAddServerBySSURL(e.Url);

            if (!string.IsNullOrWhiteSpace(Options.OpenUrl))
            {
                MainController.AskAddServerBySSURL(Options.OpenUrl);
            }
            #endregion

            Application.Run();
        }

        private static int exited;
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (Interlocked.Increment(ref exited) == 1)
            {
                string errMsg = e.ExceptionObject.ToString();
                logger.Error(errMsg);
                MessageBox.Show(
                    $"{I18N.GetString("Unexpected error, shadowsocks will exit. Please report to")} https://github.com/shadowsocks/shadowsocks-windows/issues {Environment.NewLine}{errMsg}",
                    "Shadowsocks non-UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private static void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
        {
            if (Interlocked.Increment(ref exited) == 1)
            {
                string errorMsg = $"Exception Detail: {Environment.NewLine}{e.Exception}";
                logger.Error(errorMsg);
                MessageBox.Show(
                    $"{I18N.GetString("Unexpected error, shadowsocks will exit. Please report to")} https://github.com/shadowsocks/shadowsocks-windows/issues {Environment.NewLine}{errorMsg}",
                    "Shadowsocks UI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }
        }

        private static void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    logger.Info("os wake up");
                    if (MainController != null)
                    {
                        Task.Factory.StartNew(() =>
                        {
                            Thread.Sleep(10 * 1000);
                            try
                            {
                                MainController.Start(true);
                                logger.Info("controller started");
                            }
                            catch (Exception ex)
                            {
                                logger.LogUsefulException(ex);
                            }
                        });
                    }
                    break;
                case PowerModes.Suspend:
                    if (MainController != null)
                    {
                        MainController.Stop();
                        logger.Info("controller stopped");
                    }
                    logger.Info("os suspend");
                    break;
            }
        }

        private static void Application_ApplicationExit(object sender, EventArgs e)
        {
            // Detach static event handlers.
            Application.ApplicationExit -= Application_ApplicationExit;
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            Application.ThreadException -= Application_ThreadException;
            HotKeys.Destroy();
            if (MainController != null)
            {
                MainController.Stop();
                MainController = null;
            }
        }
    }
}
