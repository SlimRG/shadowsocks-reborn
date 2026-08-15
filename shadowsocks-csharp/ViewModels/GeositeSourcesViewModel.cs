using ReactiveUI;
using Shadowsocks.Controller;
using Shadowsocks.Controller.Service;
using Shadowsocks.Model;
using Shadowsocks.View;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;

namespace Shadowsocks.ViewModels
{
    public class GeositeSourcesViewModel : ReactiveObject
    {
        private readonly ShadowsocksController _controller;
        private readonly MenuViewController _menuViewController;

        public GeositeSourcesViewModel()
        {
            _controller = Program.MainController;
            _menuViewController = Program.MenuController;

            var config = _controller.GetCurrentConfiguration();
            Sources = new ObservableCollection<string>(
                Configuration.NormalizeGeositeSourceList(config.geositeUrls));
            SelectedSource = Sources.FirstOrDefault();
            Address = "";

            var hasSelection = this.WhenAnyValue(
                x => x.SelectedSource,
                selected => !string.IsNullOrWhiteSpace(selected));
            var canRemove = this.WhenAnyValue(
                x => x.SelectedSource,
                x => x.Sources.Count,
                (selected, count) => !string.IsNullOrWhiteSpace(selected) && count > 1);
            var canAdd = this.WhenAnyValue(
                x => x.Address,
                address => ParseAddresses(address).Count > 0 && ParseAddresses(address).All(IsValidSource));

            Add = ReactiveCommand.Create(AddSources, canAdd);
            Remove = ReactiveCommand.Create(RemoveSelected, canRemove);
            MoveUp = ReactiveCommand.Create(MoveSelectedUp, hasSelection);
            MoveDown = ReactiveCommand.Create(MoveSelectedDown, hasSelection);
            RestoreDefault = ReactiveCommand.Create(RestoreDefaultSource);
            Save = ReactiveCommand.Create(() =>
            {
                _controller.SaveGeositeSources(Sources.ToList());
                _menuViewController.CloseGeositeSourcesWindow();
            });
            Cancel = ReactiveCommand.Create(_menuViewController.CloseGeositeSourcesWindow);
        }

        public ObservableCollection<string> Sources { get; }

        private string _selectedSource;
        public string SelectedSource
        {
            get => _selectedSource;
            set => this.RaiseAndSetIfChanged(ref _selectedSource, value);
        }

        private string _address;
        public string Address
        {
            get => _address;
            set => this.RaiseAndSetIfChanged(ref _address, value);
        }

        public ReactiveCommand<Unit, Unit> Add { get; }
        public ReactiveCommand<Unit, Unit> Remove { get; }
        public ReactiveCommand<Unit, Unit> MoveUp { get; }
        public ReactiveCommand<Unit, Unit> MoveDown { get; }
        public ReactiveCommand<Unit, Unit> RestoreDefault { get; }
        public ReactiveCommand<Unit, Unit> Save { get; }
        public ReactiveCommand<Unit, Unit> Cancel { get; }

        private void AddSources()
        {
            foreach (string source in ParseAddresses(Address))
            {
                if (Sources.Any(existing => string.Equals(existing, source, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Sources.Add(source);
                SelectedSource = source;
            }
            Address = "";
        }

        private void RemoveSelected()
        {
            if (string.IsNullOrWhiteSpace(SelectedSource) || Sources.Count <= 1)
                return;

            int index = Sources.IndexOf(SelectedSource);
            if (index < 0)
                return;

            Sources.RemoveAt(index);
            SelectedSource = Sources[Math.Min(index, Sources.Count - 1)];
        }

        private void MoveSelectedUp()
        {
            int index = Sources.IndexOf(SelectedSource);
            if (index <= 0)
                return;

            Sources.Move(index, index - 1);
        }

        private void MoveSelectedDown()
        {
            int index = Sources.IndexOf(SelectedSource);
            if (index < 0 || index >= Sources.Count - 1)
                return;

            Sources.Move(index, index + 1);
        }

        private void RestoreDefaultSource()
        {
            Sources.Clear();
            Sources.Add(GeositeUpdater.DefaultSourceUrl);
            SelectedSource = Sources[0];
            Address = "";
        }

        private static List<string> ParseAddresses(string value)
            => (value ?? "")
                .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(source => source.Trim())
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static bool IsValidSource(string source)
            => Uri.TryCreate(source, UriKind.Absolute, out Uri uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
    }
}
