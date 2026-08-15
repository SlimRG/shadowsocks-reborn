using System.Reactive;
using System.Text;
using System.Windows.Input;
using ReactiveUI;
using Shadowsocks.Controller;
using Shadowsocks.Model;
using Shadowsocks.View;

namespace Shadowsocks.ViewModels
{
    public class HotkeysViewModel : ReactiveObject
    {
        public HotkeysViewModel()
        {
            _config = Program.MainController.GetCurrentConfiguration();
            _controller = Program.MainController;
            _menuViewController = Program.MenuController;

            HotkeySystemProxy = _config.hotkey.SwitchSystemProxy;
            HotkeyProxyMode = _config.hotkey.SwitchSystemProxyMode;
            HotkeyAllowLan = _config.hotkey.SwitchAllowLan;
            HotkeyOpenLogs = _config.hotkey.ShowLogs;
            HotkeySwitchPrev = _config.hotkey.ServerMoveUp;
            HotkeySwitchNext = _config.hotkey.ServerMoveDown;
            RegisterAtStartup = _config.hotkey.RegHotkeysAtStartup;

            HotkeySystemProxyStatus = "✔";
            HotkeyProxyModeStatus = "✔";
            HotkeyAllowLanStatus = "✔";
            HotkeyOpenLogsStatus = "✔";
            HotkeySwitchPrevStatus = "✔";
            HotkeySwitchNextStatus = "✔";

            RegisterAll = ReactiveCommand.Create(() => RegisterAllAndUpdateStatus());
            Save = ReactiveCommand.Create(() => RegisterAllAndUpdateStatus(true));
            Cancel = ReactiveCommand.Create(_menuViewController.CloseHotkeysWindow);
        }

        private readonly Configuration _config;
        private readonly ShadowsocksController _controller;
        private readonly MenuViewController _menuViewController;

        public ReactiveCommand<Unit, Unit> RegisterAll { get; }
        public ReactiveCommand<Unit, Unit> Save { get; }
        public ReactiveCommand<Unit, Unit> Cancel { get; }

        private string _hotkeySystemProxy;
        public string HotkeySystemProxy
        {
            get => _hotkeySystemProxy;
            set => this.RaiseAndSetIfChanged(ref _hotkeySystemProxy, value);
        }

        private string _hotkeyProxyMode;
        public string HotkeyProxyMode
        {
            get => _hotkeyProxyMode;
            set => this.RaiseAndSetIfChanged(ref _hotkeyProxyMode, value);
        }

        private string _hotkeyAllowLan;
        public string HotkeyAllowLan
        {
            get => _hotkeyAllowLan;
            set => this.RaiseAndSetIfChanged(ref _hotkeyAllowLan, value);
        }

        private string _hotkeyOpenLogs;
        public string HotkeyOpenLogs
        {
            get => _hotkeyOpenLogs;
            set => this.RaiseAndSetIfChanged(ref _hotkeyOpenLogs, value);
        }

        private string _hotkeySwitchPrev;
        public string HotkeySwitchPrev
        {
            get => _hotkeySwitchPrev;
            set => this.RaiseAndSetIfChanged(ref _hotkeySwitchPrev, value);
        }

        private string _hotkeySwitchNext;
        public string HotkeySwitchNext
        {
            get => _hotkeySwitchNext;
            set => this.RaiseAndSetIfChanged(ref _hotkeySwitchNext, value);
        }

        private bool _registerAtStartup;
        public bool RegisterAtStartup
        {
            get => _registerAtStartup;
            set => this.RaiseAndSetIfChanged(ref _registerAtStartup, value);
        }

        private string _hotkeySystemProxyStatus;
        public string HotkeySystemProxyStatus
        {
            get => _hotkeySystemProxyStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeySystemProxyStatus, value);
        }

        private string _hotkeyProxyModeStatus;
        public string HotkeyProxyModeStatus
        {
            get => _hotkeyProxyModeStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeyProxyModeStatus, value);
        }

        private string _hotkeyAllowLanStatus;
        public string HotkeyAllowLanStatus
        {
            get => _hotkeyAllowLanStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeyAllowLanStatus, value);
        }

        private string _hotkeyOpenLogsStatus;
        public string HotkeyOpenLogsStatus
        {
            get => _hotkeyOpenLogsStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeyOpenLogsStatus, value);
        }

        private string _hotkeySwitchPrevStatus;
        public string HotkeySwitchPrevStatus
        {
            get => _hotkeySwitchPrevStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeySwitchPrevStatus, value);
        }

        private string _hotkeySwitchNextStatus;
        public string HotkeySwitchNextStatus
        {
            get => _hotkeySwitchNextStatus;
            set => this.RaiseAndSetIfChanged(ref _hotkeySwitchNextStatus, value);
        }

        public void RecordKeyDown(int hotkeyIndex, KeyEventArgs keyEventArgs)
        {
            var recordedKeyStringBuilder = new StringBuilder();

            // record modifiers
            if ((Keyboard.Modifiers & ModifierKeys.Control) > 0)
                recordedKeyStringBuilder.Append("Ctrl+");
            if ((Keyboard.Modifiers & ModifierKeys.Alt) > 0)
                recordedKeyStringBuilder.Append("Alt+");
            if ((Keyboard.Modifiers & ModifierKeys.Shift) > 0)
                recordedKeyStringBuilder.Append("Shift+");

            // record other keys when at least one modifier is pressed
            if (recordedKeyStringBuilder.Length > 0 && (keyEventArgs.Key < Key.LeftShift || keyEventArgs.Key > Key.RightAlt))
                recordedKeyStringBuilder.Append(keyEventArgs.Key);

            switch (hotkeyIndex)
            {
                case 0:
                    HotkeySystemProxy = recordedKeyStringBuilder.ToString();
                    break;
                case 1:
                    HotkeyProxyMode = recordedKeyStringBuilder.ToString();
                    break;
                case 2:
                    HotkeyAllowLan = recordedKeyStringBuilder.ToString();
                    break;
                case 3:
                    HotkeyOpenLogs = recordedKeyStringBuilder.ToString();
                    break;
                case 4:
                    HotkeySwitchPrev = recordedKeyStringBuilder.ToString();
                    break;
                case 5:
                    HotkeySwitchNext = recordedKeyStringBuilder.ToString();
                    break;
            }
        }

        public void FinishOnKeyUp(int hotkeyIndex, KeyEventArgs keyEventArgs)
        {
            switch (hotkeyIndex)
            {
                case 0:
                    if (HotkeySystemProxy.EndsWith("+"))
                        HotkeySystemProxy = "";
                    break;
                case 1:
                    if (HotkeyProxyMode.EndsWith("+"))
                        HotkeyProxyMode = "";
                    break;
                case 2:
                    if (HotkeyAllowLan.EndsWith("+"))
                        HotkeyAllowLan = "";
                    break;
                case 3:
                    if (HotkeyOpenLogs.EndsWith("+"))
                        HotkeyOpenLogs = "";
                    break;
                case 4:
                    if (HotkeySwitchPrev.EndsWith("+"))
                        HotkeySwitchPrev = "";
                    break;
                case 5:
                    if (HotkeySwitchNext.EndsWith("+"))
                        HotkeySwitchNext = "";
                    break;
            }
        }

        private void RegisterAllAndUpdateStatus(bool save = false)
        {
            HotkeySystemProxyStatus = HotkeyReg.RegHotkeyFromString(HotkeySystemProxy, "SwitchSystemProxyCallback") ? "✔" : "❌";
            HotkeyProxyModeStatus = HotkeyReg.RegHotkeyFromString(HotkeyProxyMode, "SwitchSystemProxyModeCallback") ? "✔" : "❌";
            HotkeyAllowLanStatus = HotkeyReg.RegHotkeyFromString(HotkeyAllowLan, "SwitchAllowLanCallback") ? "✔" : "❌";
            HotkeyOpenLogsStatus = HotkeyReg.RegHotkeyFromString(HotkeyOpenLogs, "ShowLogsCallback") ? "✔" : "❌";
            HotkeySwitchPrevStatus = HotkeyReg.RegHotkeyFromString(HotkeySwitchPrev, "ServerMoveUpCallback") ? "✔" : "❌";
            HotkeySwitchNextStatus = HotkeyReg.RegHotkeyFromString(HotkeySwitchNext, "ServerMoveDownCallback") ? "✔" : "❌";

            if (HotkeySystemProxyStatus == "✔" &&
                HotkeyProxyModeStatus == "✔" &&
                HotkeyAllowLanStatus == "✔" &&
                HotkeyOpenLogsStatus == "✔" &&
                HotkeySwitchPrevStatus == "✔" &&
                HotkeySwitchNextStatus == "✔" && save)
            {
                _controller.SaveHotkeyConfig(GetHotkeyConfig);
                _menuViewController.CloseHotkeysWindow();
            }
        }

        private HotkeyConfig GetHotkeyConfig => new HotkeyConfig()
        {
            SwitchSystemProxy = HotkeySystemProxy,
            SwitchSystemProxyMode = HotkeyProxyMode,
            SwitchAllowLan = HotkeyAllowLan,
            ShowLogs = HotkeyOpenLogs,
            ServerMoveUp = HotkeySwitchPrev,
            ServerMoveDown = HotkeySwitchNext,
            RegHotkeysAtStartup = RegisterAtStartup
        };
    }
}
