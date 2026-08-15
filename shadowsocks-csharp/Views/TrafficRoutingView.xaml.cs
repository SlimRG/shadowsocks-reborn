using Shadowsocks.Controller;
using Shadowsocks.Controller.Traffic;
using Shadowsocks.Localization;
using Shadowsocks.Model;
using Shadowsocks.View;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Shadowsocks.Views
{
    public partial class TrafficRoutingView : UserControl
    {
        public sealed class ApplicationRuleRow
        {
            public bool Enabled { get; set; } = true;
            public string Application { get; set; } = string.Empty;
            public TrafficRouteAction Action { get; set; } = TrafficRouteAction.Proxy;
        }

        public sealed record RouteChoice(TrafficRouteAction Action, string DisplayName);

        private readonly ShadowsocksController _controller;
        private readonly MenuViewController _menuController;

        public TrafficRoutingView()
        {
            InitializeComponent();
            _controller = Program.MainController;
            _menuController = Program.MenuController;

            RouteChoices =
            [
                new RouteChoice(TrafficRouteAction.Proxy, Localize("trafficRouteProxy")),
                new RouteChoice(TrafficRouteAction.Direct, Localize("trafficRouteDirect")),
                new RouteChoice(TrafficRouteAction.Block, Localize("trafficRouteBlock")),
            ];

            Configuration configuration = _controller.GetCurrentConfiguration();
            ApplicationRules = new ObservableCollection<ApplicationRuleRow>(
                (configuration.applicationRules ?? [])
                    .Where(rule => rule is not null)
                    .Select(rule => new ApplicationRuleRow
                    {
                        Enabled = rule.enabled,
                        Application = rule.application ?? string.Empty,
                        Action = rule.action == TrafficRouteAction.Default ? TrafficRouteAction.Proxy : rule.action,
                    }));

            DataContext = this;
            applicationRulesGrid.ItemsSource = ApplicationRules;
            userModeRadioButton.IsChecked = configuration.trafficCaptureMode == TrafficCaptureMode.User;
            adminModeRadioButton.IsChecked = configuration.trafficCaptureMode == TrafficCaptureMode.Admin;
            gameApplicationsTextBox.Text = string.Join(Environment.NewLine, configuration.gameModeApplications ?? []);
        }

        public ObservableCollection<ApplicationRuleRow> ApplicationRules { get; }
        public IReadOnlyList<RouteChoice> RouteChoices { get; }

        private void AddApplicationRuleButton_Click(object sender, RoutedEventArgs e)
        {
            ApplicationRuleRow row = new();
            ApplicationRules.Add(row);
            applicationRulesGrid.SelectedItem = row;
            applicationRulesGrid.ScrollIntoView(row);
        }

        private void RemoveApplicationRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (applicationRulesGrid.SelectedItem is ApplicationRuleRow row)
            {
                ApplicationRules.Remove(row);
            }
        }

        private async void SaveTrafficRoutingButton_Click(object sender, RoutedEventArgs e)
        {
            saveTrafficRoutingButton.IsEnabled = false;
            try
            {
                TrafficCaptureMode mode = adminModeRadioButton.IsChecked == true
                    ? TrafficCaptureMode.Admin
                    : TrafficCaptureMode.User;
                List<ApplicationRouteRule> rules = ApplicationRules
                    .Where(row => !string.IsNullOrWhiteSpace(row.Application))
                    .Select(row => new ApplicationRouteRule
                    {
                        enabled = row.Enabled,
                        application = row.Application.Trim(),
                        action = row.Action,
                    })
                    .ToList();
                List<string> games = SplitPatterns(gameApplicationsTextBox.Text);

                bool saved = await _controller.SaveTrafficRoutingAsync(
                    mode,
                    rules,
                    games);

                if (saved)
                {
                    _menuController.CloseTrafficRoutingWindow();
                }
                else
                {
                    userModeRadioButton.IsChecked = true;
                    adminModeRadioButton.IsChecked = false;
                }
            }
            finally
            {
                saveTrafficRoutingButton.IsEnabled = true;
            }
        }

        private void CancelTrafficRoutingButton_Click(object sender, RoutedEventArgs e)
        {
            _menuController.CloseTrafficRoutingWindow();
        }

        private static List<string> SplitPatterns(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(pattern => pattern.Trim())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string Localize(string key)
        {
            return LocalizationProvider.GetLocalizedValue<string>(key) ?? key;
        }
    }
}
