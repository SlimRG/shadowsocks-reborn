using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Shadowsocks.Model;
using Shadowsocks.Windows.Shell;
using Shadowsocks.WinUI.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using ZXing.QrCode.Internal;

namespace Shadowsocks.WinUI.Pages;

public sealed class SharingPage : Page, IRefreshablePage
{
    private readonly WinUIPageContext _context;
    private readonly ListView _servers;
    private readonly TextBox _url;
    private readonly Image _qrImage;
    private readonly TextBox _importUrl;
    private Configuration? _configuration;
    private bool _refreshing;

    internal SharingPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Share / QR",
            "Share a server using the same list + QR workflow as the original client, or import ss:// links.",
            out StackPanel panel);

        var shareGrid = new Grid { ColumnSpacing = 20, RowSpacing = 12 };
        shareGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        shareGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shareGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shareGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var qrHost = new Border
        {
            MinHeight = 340,
            Padding = new Thickness(12),
            Background = WinUIStyles.GetBrush("ControlFillColorDefaultBrush"),
            CornerRadius = new CornerRadius(6),
        };
        _qrImage = new Image
        {
            Width = 320,
            Height = 320,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        qrHost.Child = _qrImage;
        shareGrid.Children.Add(qrHost);

        var serverHost = new StackPanel { Spacing = 8 };
        serverHost.Children.Add(WinUIStyles.CreateSectionTitle("Servers"));
        _servers = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 340,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _servers.SelectionChanged += OnServerSelectionChanged;
        serverHost.Children.Add(_servers);
        Grid.SetColumn(serverHost, 1);
        shareGrid.Children.Add(serverHost);

        var urlGrid = new Grid { ColumnSpacing = 8 };
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _url = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            PlaceholderText = "ss://",
        };
        urlGrid.Children.Add(_url);
        var copy = new Button { Content = "Copy", MinWidth = 92 };
        _context.SetToolTip(copy, "Copy the selected server as an ss:// URL.");
        copy.Click += OnCopyClicked;
        Grid.SetColumn(copy, 1);
        urlGrid.Children.Add(copy);
        Grid.SetRow(urlGrid, 1);
        Grid.SetColumnSpan(urlGrid, 2);
        shareGrid.Children.Add(urlGrid);
        panel.Children.Add(WinUIStyles.CreateCard(shareGrid));

        var importCard = new StackPanel { Spacing = 10 };
        importCard.Children.Add(WinUIStyles.CreateSectionTitle("Import server"));
        _importUrl = new TextBox
        {
            Header = "ss:// link",
            PlaceholderText = "Paste one or more ss:// links",
            AcceptsReturn = true,
            MinHeight = 74,
            TextWrapping = TextWrapping.Wrap,
        };
        _context.SetToolTip(_importUrl, "Paste one or more ss:// server links, one per line.");
        importCard.Children.Add(_importUrl);

        var urlActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var import = new Button { Content = "Import", HorizontalAlignment = HorizontalAlignment.Left };
        _context.SetToolTip(import, "Import every valid ss:// link from the text box.");
        import.Click += OnImportClicked;
        urlActions.Children.Add(import);
        var pasteUrl = new Button { Content = "Paste URL from clipboard" };
        _context.SetToolTip(pasteUrl, "Paste text from the clipboard into the ss:// import field.");
        pasteUrl.Click += OnPasteUrlClicked;
        urlActions.Children.Add(pasteUrl);
        importCard.Children.Add(urlActions);

        var qrActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var scanScreen = new Button { Content = "Scan QRCode from Screen" };
        _context.SetToolTip(scanScreen, "Capture the screen and import the first supported Shadowsocks QR code found.");
        scanScreen.Click += OnScanScreenClicked;
        qrActions.Children.Add(scanScreen);
        var openImage = new Button { Content = "Open image" };
        _context.SetToolTip(openImage, "Choose an image file and scan it for a Shadowsocks QR code.");
        openImage.Click += OnOpenImageClicked;
        qrActions.Children.Add(openImage);
        var clipboardImage = new Button { Content = "Scan QR from clipboard image" };
        _context.SetToolTip(clipboardImage, "Scan the image currently stored in the clipboard for a Shadowsocks QR code.");
        clipboardImage.Click += OnClipboardImageClicked;
        qrActions.Children.Add(clipboardImage);
        importCard.Children.Add(qrActions);
        panel.Children.Add(WinUIStyles.CreateCard(importCard));

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
        if (configuration?.configs is null)
        {
            return;
        }

        _configuration = configuration;
        _refreshing = true;
        try
        {
            _servers.Items.Clear();
            for (int index = 0; index < configuration.configs.Count; index++)
            {
                Server server = configuration.configs[index];
                _servers.Items.Add(CreateServerListItem(server, index));
            }
            if (_servers.Items.Count > 0)
            {
                _servers.SelectedIndex = Math.Clamp(configuration.index, 0, _servers.Items.Count - 1);
            }
        }
        finally
        {
            _refreshing = false;
        }
        UpdateSharedServer();
    }

    public void SelectServer(int index)
    {
        Refresh();
        if (_servers.Items.Count > 0)
        {
            _servers.SelectedIndex = Math.Clamp(index, 0, _servers.Items.Count - 1);
        }
    }

    private ListViewItem CreateServerListItem(Server server, int index)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = server.IsConfigured ? server.ToString() : _context.LF("Server {0}", index + 1),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        row.Children.Add(label);

        var delete = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 11 },
            Tag = index,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _context.SetToolTip(delete, "Delete the selected server.");
        delete.Click += OnDeleteServerClicked;
        Grid.SetColumn(delete, 1);
        row.Children.Add(delete);

        return new ListViewItem
        {
            Content = row,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 4, 4, 4),
        };
    }

    public async Task ImportUrlAsync(string value)
    {
        if (_context.Controller is null)
        {
            return;
        }

        value = value?.Trim() ?? string.Empty;
        _importUrl.Text = value;
        if (Server.GetServers(value).Count == 0)
        {
            _context.ShowInfo("Import", "No valid ss:// server links were found.", InfoBarSeverity.Error);
            return;
        }

        XamlRoot? xamlRoot = _context.GetXamlRoot();
        if (xamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _context.L("Import server configuration?"),
            Content = _context.L("Import from URL:") + "\n" + value,
            PrimaryButtonText = _context.L("Import"),
            CloseButtonText = _context.L("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (_context.Controller.AddServerBySSURL(value))
        {
            _importUrl.Text = string.Empty;
            _context.ShowInfo("Import", "Server configuration imported.", InfoBarSeverity.Success);
            Refresh();
        }
        else
        {
            _context.ShowInfo("Import", "Failed to import. Please check if the link is valid.", InfoBarSeverity.Error);
        }
    }

    private void OnDeleteServerClicked(object sender, RoutedEventArgs _1)
    {
        if (_context.Controller is null || sender is not Button { Tag: int index })
        {
            return;
        }

        if (_context.Controller.RemoveServerAt(index))
        {
            _context.ShowInfo("Servers", "Server deleted.", InfoBarSeverity.Success);
            Refresh();
        }
    }

    private async void OnPasteUrlClicked(object _, RoutedEventArgs _1)
    {
        DataPackageView content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text))
        {
            _context.ShowInfo("Import", "Clipboard does not contain text", InfoBarSeverity.Warning);
            return;
        }

        _importUrl.Text = (await content.GetTextAsync()).Trim();
    }

    private async void OnScanScreenClicked(object _, RoutedEventArgs _1)
        => await HandleScannedValueAsync(ScreenQrCodeScanner.ScanFromScreen());

    private async void OnOpenImageClicked(object _, RoutedEventArgs _1)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".tif");
        picker.FileTypeFilter.Add(".tiff");

        nint windowHandle = _context.GetWindowHandle();
        if (windowHandle != nint.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        }

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            using var randomStream = await file.OpenReadAsync();
            using Stream stream = randomStream.AsStreamForRead();
            await HandleScannedValueAsync(ScreenQrCodeScanner.ScanFromStream(stream));
        }
        catch (Exception exception)
        {
            _context.ShowInfo("QR code", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnClipboardImageClicked(object _, RoutedEventArgs _1)
    {
        try
        {
            (bool hasImage, string? result) = await ScreenQrCodeScanner.ScanFromClipboardAsync();
            if (!hasImage)
            {
                _context.ShowInfo("QR code", "Clipboard does not contain an image.", InfoBarSeverity.Warning);
                return;
            }

            await HandleScannedValueAsync(result);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("QR code", exception.Message, InfoBarSeverity.Error);
        }
    }

    private async Task HandleScannedValueAsync(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            _context.ShowInfo("QR code", "No QRCode found. Try to zoom in or move it to the center of the screen.", InfoBarSeverity.Warning);
            return;
        }

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
            return;
        }

        await ImportUrlAsync(value);
    }

    private void OnServerSelectionChanged(object _, SelectionChangedEventArgs _1)
    {
        if (!_refreshing)
        {
            UpdateSharedServer();
        }
    }

    private async void UpdateSharedServer()
    {
        if (_configuration?.configs is null
            || _servers.SelectedIndex < 0
            || _servers.SelectedIndex >= _configuration.configs.Count)
        {
            _url.Text = string.Empty;
            _qrImage.Source = null;
            return;
        }

        Server server = _configuration.configs[_servers.SelectedIndex];
        if (!server.IsConfigured)
        {
            _url.Text = _context.L("Server is not configured.");
            _qrImage.Source = null;
            return;
        }

        string url = server.GetURL(_configuration.generateLegacyUrl);
        _url.Text = url;
        try
        {
            _qrImage.Source = await CreateQrSvgSourceAsync(url);
        }
        catch (Exception exception)
        {
            _qrImage.Source = null;
            _context.ShowInfo("QR code", exception.Message, InfoBarSeverity.Warning);
        }
    }

    private void OnCopyClicked(object _, RoutedEventArgs _1)
    {
        if (string.IsNullOrWhiteSpace(_url.Text) || !_url.Text.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(_url.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        _context.ShowInfo("Share", "Server link copied to the clipboard.", InfoBarSeverity.Success);
    }

    private async void OnImportClicked(object _, RoutedEventArgs _1)
        => await ImportUrlAsync(_importUrl.Text ?? string.Empty);

    private static async Task<SvgImageSource> CreateQrSvgSourceAsync(string text)
    {
        QRCode code = ZXing.QrCode.Internal.Encoder.encode(text, ErrorCorrectionLevel.L);
        var matrix = code.Matrix;
        const int quietZone = 4;
        int width = matrix.Width + quietZone * 2;
        int height = matrix.Height + quietZone * 2;

        var path = new StringBuilder();
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                if (matrix[x, y] != 0)
                {
                    path.Append("M").Append(x + quietZone).Append(' ').Append(y + quietZone).Append("h1v1h-1z");
                }
            }
        }

        string svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" shape-rendering=\"crispEdges\"><rect width=\"100%\" height=\"100%\" fill=\"white\"/><path d=\"{path}\" fill=\"black\"/></svg>";
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.UnicodeEncoding = global::Windows.Storage.Streams.UnicodeEncoding.Utf8;
            writer.WriteString(svg);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var source = new SvgImageSource();
        SvgImageSourceLoadStatus status = await source.SetSourceAsync(stream);
        if (status != SvgImageSourceLoadStatus.Success)
        {
            throw new InvalidOperationException($"WinUI could not decode the generated QR SVG ({status}).");
        }
        return source;
    }
}
