using ReactiveUI;
using Shadowsocks.ViewModels;
using System.Reactive.Disposables;

namespace Shadowsocks.Views
{
    public partial class GeositeSourcesView : ReactiveUserControl<GeositeSourcesViewModel>
    {
        public GeositeSourcesView()
        {
            InitializeComponent();
            ViewModel = new GeositeSourcesViewModel();

            this.WhenActivated(disposables =>
            {
                this.OneWayBind(ViewModel,
                    viewModel => viewModel.Sources,
                    view => view.sourcesListBox.ItemsSource)
                    .DisposeWith(disposables);
                this.Bind(ViewModel,
                    viewModel => viewModel.SelectedSource,
                    view => view.sourcesListBox.SelectedItem)
                    .DisposeWith(disposables);
                this.Bind(ViewModel,
                    viewModel => viewModel.Address,
                    view => view.addressTextBox.Text)
                    .DisposeWith(disposables);

                this.BindCommand(ViewModel, viewModel => viewModel.Add, view => view.addButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.Remove, view => view.removeButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.MoveUp, view => view.moveUpButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.MoveDown, view => view.moveDownButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.RestoreDefault, view => view.restoreDefaultButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.Save, view => view.saveButton)
                    .DisposeWith(disposables);
                this.BindCommand(ViewModel, viewModel => viewModel.Cancel, view => view.cancelButton)
                    .DisposeWith(disposables);
            });
        }
    }
}
