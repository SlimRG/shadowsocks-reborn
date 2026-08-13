using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Shadowsocks.Controller;
using Shadowsocks.Localization;
using Shadowsocks.Model;
using Shadowsocks.View;

namespace Shadowsocks.ViewModels
{
    public class ForwardProxyViewModel : ReactiveValidationObject
    {
        public ForwardProxyViewModel()
        {
            _config = Program.MainController.GetCurrentConfiguration();
            _controller = Program.MainController;
            _menuViewController = Program.MenuController;

            if (!_config.proxy.useProxy)
            {
                NoProxy = true;
            }
            else if (_config.proxy.proxyType == 0)
            {
                UseSocks5Proxy = true;
            }
            else
            {
                UseHttpProxy = true;
            }

            Address = _config.proxy.proxyServer;
            Port = _config.proxy.proxyPort;
            Timeout = _config.proxy.proxyTimeout;

            Username = _config.proxy.authUser;
            Password = _config.proxy.authPwd;

            _canModifyDetails = this.WhenAnyValue(x => x.NoProxy, x => !x)
                .ToProperty(this, x => x.CanModifyDetails);

            AddressRule = this.ValidationRule(
                viewModel => viewModel.Address,
                address => !string.IsNullOrWhiteSpace(address),
                LocalizationProvider.GetLocalizedValue<string>("ForwardProxyAddressRequired"));
            PortRule = this.ValidationRule(
                viewModel => viewModel.Port,
                port => port > 0 && port <= 65535,
                port => string.Format(LocalizationProvider.GetLocalizedValue<string>("ForwardProxyPortOutOfRange"), port));
            TimeoutRule = this.ValidationRule(
                viewModel => viewModel.Timeout,
                timeout => timeout > 0 && timeout <= 10,
                timeout => string.Format(LocalizationProvider.GetLocalizedValue<string>("ForwardProxyTimeoutOutOfRange"), timeout));

            var authValid = this
                .WhenAnyValue(x => x.Username, x => x.Password, (username, password) => new { Username = username, Password = password })
                .Select(x => string.IsNullOrWhiteSpace(x.Username) == string.IsNullOrWhiteSpace(x.Password));
            AuthRule = this.ValidationRule(authValid, LocalizationProvider.GetLocalizedValue<string>("ForwardProxyCredentialsPairRequired"));

            var canSave = this.IsValid();

            Save = ReactiveCommand.Create(() =>
            {
                _controller.SaveProxy(GetForwardProxyConfig());
                _menuViewController.CloseForwardProxyWindow();
            }, canSave);
            Cancel = ReactiveCommand.Create(_menuViewController.CloseForwardProxyWindow);
        }

        private readonly Configuration _config;
        private readonly ShadowsocksController _controller;
        private readonly MenuViewController _menuViewController;

        public ValidationHelper AddressRule { get; }
        public ValidationHelper PortRule { get; }
        public ValidationHelper TimeoutRule { get; }
        public ValidationHelper AuthRule { get; }

        public ReactiveCommand<Unit, Unit> Save { get; }
        public ReactiveCommand<Unit, Unit> Cancel { get; }

        private readonly ObservableAsPropertyHelper<bool> _canModifyDetails;
        public bool CanModifyDetails => _canModifyDetails.Value;

        private bool _noProxy;
        public bool NoProxy
        {
            get => _noProxy;
            set => this.RaiseAndSetIfChanged(ref _noProxy, value);
        }

        private bool _useSocks5Proxy;
        public bool UseSocks5Proxy
        {
            get => _useSocks5Proxy;
            set => this.RaiseAndSetIfChanged(ref _useSocks5Proxy, value);
        }

        private bool _useHttpProxy;
        public bool UseHttpProxy
        {
            get => _useHttpProxy;
            set => this.RaiseAndSetIfChanged(ref _useHttpProxy, value);
        }

        private string _address;
        public string Address
        {
            get => _address;
            set => this.RaiseAndSetIfChanged(ref _address, value);
        }

        private int _port;
        public int Port
        {
            get => _port;
            set => this.RaiseAndSetIfChanged(ref _port, value);
        }

        private int _timeout;
        public int Timeout
        {
            get => _timeout;
            set => this.RaiseAndSetIfChanged(ref _timeout, value);
        }

        private string _username;
        public string Username
        {
            get => _username;
            set => this.RaiseAndSetIfChanged(ref _username, value);
        }

        private string _password;
        public string Password
        {
            get => _password;
            set => this.RaiseAndSetIfChanged(ref _password, value);
        }

        private ForwardProxyConfig GetForwardProxyConfig()
        {
            var forwardProxyConfig = new ForwardProxyConfig()
            {
                proxyServer = Address,
                proxyPort = Port,
                proxyTimeout = Timeout,
                authUser = Username,
                authPwd = Password
            };
            if (NoProxy)
            {
                forwardProxyConfig.useProxy = false;
            }
            else if (UseSocks5Proxy)
            {
                forwardProxyConfig.useProxy = true;
                forwardProxyConfig.proxyType = 0;
            }
            else
            {
                forwardProxyConfig.useProxy = true;
                forwardProxyConfig.proxyType = 1;
            }
            return forwardProxyConfig;
        }
    }
}
