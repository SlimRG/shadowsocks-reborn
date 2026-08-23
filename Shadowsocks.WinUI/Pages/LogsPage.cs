using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Shadowsocks.Controller;
using Shadowsocks.Core.Logging;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class LogsPage : Page, IRefreshablePage, IDisposable
{
    private const int MaxTailBytes = 256 * 1024;
    private const int MaxVisibleLogLines = 750;
    private const double LogViewerHeight = 360;
    private const int FontStyleBold = 1;
    private const int FontStyleItalic = 2;
    private const int TrafficWindowSeconds = 60;
    private const string SparkLevels = "▁▂▃▄▅▆▇█";

    private readonly WinUIPageContext _context;
    private readonly TextBlock _logPath;
    private readonly TextBlock _traffic;
    private readonly TextBlock _chartScale;
    private readonly TextBlock _chartCurrent;
    private readonly TextBlock _inboundGraph;
    private readonly TextBlock _outboundGraph;
    private readonly StackPanel _toolbar;
    private readonly RichTextBlock _viewer;
    private readonly ScrollViewer _viewerScroll;
    private readonly ToggleSwitch _autoRefresh;
    private readonly ToggleSwitch _verboseToggle;
    private readonly ToggleSwitch _pluginOutputToggle;
    private readonly ToggleSwitch _dnsLogsToggle;
    private readonly CheckBox _wrapText;
    private readonly CheckBox _topMost;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _viewerConfigSaveTimer;
    private readonly Lock _loggingSettingsGate = new();

    private long[] _inboundSamples = [];
    private long[] _outboundSamples = [];
    private bool _refreshing;
    private bool _trafficSubscribed;
    private long _lastRenderedLogLength = -1;
    private DateTime _lastRenderedLogWriteUtc = DateTime.MinValue;
    private string _lastViewerText = string.Empty;
    private bool _lastViewerIsStandaloneMessage;
    private Action? _cancelLogRefresh;
    private bool _disposed;
    private int _logRefreshGeneration;
    private int _loggingPreferenceWrites;
    private LogViewerConfig? _pendingViewerConfig;

    private enum LogLineSeverity
    {
        Normal,
        Warning,
        Error,
    }

    internal LogsPage(WinUIPageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        Content = WinUIStyles.CreatePage(
            "Logs",
            "Live log viewer with traffic history, wrap, toolbar, and always-on-top settings.",
            out StackPanel panel);

        var header = new StackPanel { Spacing = 10 };
        header.Children.Add(WinUIStyles.CreateSectionTitle("Log Viewer"));
        _logPath = WinUIStyles.CreateText(LoggingConfigurator.LogFilePath, "CaptionTextBlockStyle");
        _logPath.Opacity = 0.72;
        header.Children.Add(_logPath);
        _traffic = WinUIStyles.CreateText("In: 0 B · Out: 0 B", "BodyStrongTextBlockStyle");
        header.Children.Add(_traffic);

        _toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var clear = new Button { Content = "Clear logs" };
        _context.SetToolTip(clear, "Clear the current log file and the text shown in this viewer.");
        clear.Click += (_, _) => ClearLogs();
        _toolbar.Children.Add(clear);
        var refresh = new Button { Content = "Refresh" };
        _context.SetToolTip(refresh, "Reload the latest log contents immediately.");
        refresh.Click += (_, _) => QueueLogRefresh(force: true);
        _toolbar.Children.Add(refresh);
        var openFile = new Button { Content = "Open file" };
        _context.SetToolTip(openFile, "Open the active log file with the default Windows application.");
        openFile.Click += (_, _) => OpenPath(LoggingConfigurator.LogFilePath);
        _toolbar.Children.Add(openFile);
        var openFolder = new Button { Content = "Open location" };
        _context.SetToolTip(openFolder, "Open the folder that contains Shadowsocks log files.");
        openFolder.Click += (_, _) => OpenPath(Path.GetDirectoryName(LoggingConfigurator.LogFilePath));
        _toolbar.Children.Add(openFolder);
        header.Children.Add(_toolbar);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
        _wrapText = new CheckBox { Content = "Wrap text" };
        _context.SetToolTip(_wrapText, "Wrap long log lines instead of requiring horizontal scrolling.");
        _wrapText.Checked += OnViewerOptionChanged;
        _wrapText.Unchecked += OnViewerOptionChanged;
        options.Children.Add(_wrapText);

        _topMost = new CheckBox { Content = "Top most" };
        _context.SetToolTip(_topMost, "Keep the main Shadowsocks window above other windows while viewing logs.");
        _topMost.Checked += OnViewerOptionChanged;
        _topMost.Unchecked += OnViewerOptionChanged;
        options.Children.Add(_topMost);

        _autoRefresh = new ToggleSwitch
        {
            Header = "Auto-refresh",
            IsOn = true,
            OnContent = "Every 2 seconds",
            OffContent = "Paused",
        };
        _context.SetToolTip(_autoRefresh, "Refresh the log viewer automatically every two seconds.");
        _autoRefresh.Toggled += (_, _) => UpdateTimer();
        options.Children.Add(_autoRefresh);
        header.Children.Add(options);
        panel.Children.Add(WinUIStyles.CreateCard(header));

        var loggingOptions = new StackPanel { Spacing = 12 };
        loggingOptions.Children.Add(WinUIStyles.CreateSectionTitle("Logging"));
        var loggingOptionsRow = new Grid { ColumnSpacing = 24 };
        loggingOptionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        loggingOptionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        loggingOptionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _verboseToggle = new ToggleSwitch
        {
            Header = "Verbose Logging",
            OnContent = "Debug",
            OffContent = "Info",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _context.SetToolTip(_verboseToggle, "Enable debug-level application logging. This can significantly increase log volume.");
        _verboseToggle.Toggled += OnVerboseToggled;
        loggingOptionsRow.Children.Add(_verboseToggle);

        _pluginOutputToggle = new ToggleSwitch
        {
            Header = "Show Plugin Output",
            OnContent = "On",
            OffContent = "Off",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _context.SetToolTip(_pluginOutputToggle, "Include SIP003 plugin stdout and stderr events in the Shadowsocks log.");
        _pluginOutputToggle.Toggled += OnPluginOutputToggled;
        Grid.SetColumn(_pluginOutputToggle, 1);
        loggingOptionsRow.Children.Add(_pluginOutputToggle);

        _dnsLogsToggle = new ToggleSwitch
        {
            Header = "Show DNS Logs",
            OnContent = "On",
            OffContent = "Off",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _context.SetToolTip(_dnsLogsToggle, "Show DNSCrypt and DoH runtime diagnostics in the log viewer and allow DNS runtime output to be written. Resolver probe diagnostics and RTT details also require Verbose Logging.");
        _dnsLogsToggle.Toggled += OnDnsLogsToggled;
        Grid.SetColumn(_dnsLogsToggle, 2);
        loggingOptionsRow.Children.Add(_dnsLogsToggle);
        loggingOptions.Children.Add(loggingOptionsRow);
        panel.Children.Add(WinUIStyles.CreateCard(loggingOptions));

        var chart = new StackPanel { Spacing = 6 };
        chart.Children.Add(WinUIStyles.CreateSectionTitle("Traffic · last 60 seconds"));
        _chartCurrent = WinUIStyles.CreateText("In 0 B/s · Out 0 B/s", "CaptionTextBlockStyle");
        chart.Children.Add(_chartCurrent);
        _chartScale = WinUIStyles.CreateText("Scale 1 KiB/s", "CaptionTextBlockStyle");
        chart.Children.Add(_chartScale);
        _inboundGraph = CreateTrafficGraphText(_context.L("Inbound") + "  ");
        _outboundGraph = CreateTrafficGraphText(_context.L("Outbound") + " ");
        chart.Children.Add(_inboundGraph);
        chart.Children.Add(_outboundGraph);
        panel.Children.Add(WinUIStyles.CreateCard(chart));

        _viewer = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = PointsToDip(8F),
            Padding = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _viewerScroll = new ScrollViewer
        {
            Height = LogViewerHeight,
            Content = _viewer,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        panel.Children.Add(WinUIStyles.CreateCard(_viewerScroll));

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => QueueLogRefresh();

        _viewerConfigSaveTimer = DispatcherQueue.CreateTimer();
        _viewerConfigSaveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _viewerConfigSaveTimer.IsRepeating = false;
        _viewerConfigSaveTimer.Tick += (_, _) => FlushPendingViewerConfig();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        WinUILocalization.Apply(Content, _context.Localization);
    }

    public void Refresh()
    {
        _refreshing = true;
        try
        {
            _logPath.Text = LoggingConfigurator.LogFilePath;
            Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
            LogViewerConfig config = _pendingViewerConfig ?? configuration?.logViewer ?? new LogViewerConfig();
            _wrapText.IsChecked = config.wrapText;
            _topMost.IsChecked = config.topMost;
            if (Volatile.Read(ref _loggingPreferenceWrites) == 0)
            {
                _verboseToggle.IsOn = configuration?.isVerboseLogging == true;
                _pluginOutputToggle.IsOn = configuration?.showPluginOutput == true;
                _dnsLogsToggle.IsOn = configuration?.showDnsLogs == true;
            }
            ApplyViewerConfig(config);
            _context.SetAlwaysOnTop(config.topMost);
        }
        finally
        {
            _refreshing = false;
        }

        RefreshTrafficSnapshot();
        QueueLogRefresh();
        UpdateTimer();
    }

    private async void OnVerboseToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
            return;

        bool enabled = _verboseToggle.IsOn;
        await PersistLoggingPreferenceAsync(
            () => _context.Controller.ToggleVerboseLogging(enabled),
            "Verbose Logging").ConfigureAwait(true);
    }

    private async void OnPluginOutputToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
            return;

        bool enabled = _pluginOutputToggle.IsOn;
        await PersistLoggingPreferenceAsync(
            () => _context.Controller.ToggleShowPluginOutput(enabled),
            "Show Plugin Output").ConfigureAwait(true);
    }

    private async void OnDnsLogsToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
            return;

        bool enabled = _dnsLogsToggle.IsOn;
        await PersistLoggingPreferenceAsync(
            () => _context.Controller.ToggleShowDnsLogs(enabled),
            "Show DNS Logs").ConfigureAwait(true);
    }

    private async Task PersistLoggingPreferenceAsync(Action update, string settingName)
    {
        if (_disposed)
            return;

        Interlocked.Increment(ref _loggingPreferenceWrites);
        try
        {
            await Task.Run(() =>
            {
                lock (_loggingSettingsGate)
                {
                    update();
                }
            }).ConfigureAwait(true);
            RenderCurrentViewerText();
            QueueLogRefresh(force: true);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Logs", _context.LF("Unable to save {0}: {1}", settingName, exception.Message), InfoBarSeverity.Error);
            SyncLoggingTogglesFromConfiguration();
        }
        finally
        {
            Interlocked.Decrement(ref _loggingPreferenceWrites);
        }
    }

    private void SyncLoggingTogglesFromConfiguration()
    {
        Configuration? configuration = _context.Controller?.GetCurrentConfiguration();
        if (configuration is null)
            return;

        _refreshing = true;
        try
        {
            _verboseToggle.IsOn = configuration.isVerboseLogging;
            _pluginOutputToggle.IsOn = configuration.showPluginOutput;
            _dnsLogsToggle.IsOn = configuration.showDnsLogs;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static TextBlock CreateTrafficGraphText(string label)
    {
        return new TextBlock
        {
            Text = label + new string('▁', TrafficWindowSeconds),
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
        };
    }

    private void OnLoaded(object _, RoutedEventArgs _1)
    {
        _context.SetAlwaysOnTop(_topMost.IsChecked == true);
        SubscribeTraffic();
        RefreshTrafficSnapshot();
        QueueLogRefresh();
        UpdateTimer();
    }

    private void OnUnloaded(object _, RoutedEventArgs _1)
    {
        _timer.Stop();
        _viewerConfigSaveTimer.Stop();
        FlushPendingViewerConfig();
        CancelLogRefresh();
        UnsubscribeTraffic();
        _context.SetAlwaysOnTop(false);
    }

    private void SubscribeTraffic()
    {
        if (_trafficSubscribed || _context.Controller is null)
        {
            return;
        }

        _context.Controller.TrafficChanged += OnControllerTrafficChanged;
        _trafficSubscribed = true;
    }

    private void UnsubscribeTraffic()
    {
        if (!_trafficSubscribed || _context.Controller is null)
        {
            return;
        }

        _context.Controller.TrafficChanged -= OnControllerTrafficChanged;
        _trafficSubscribed = false;
    }

    private void OnControllerTrafficChanged(object? _, EventArgs _1)
    {
        var controller = _context.Controller;
        if (controller is null)
        {
            return;
        }

        IReadOnlyList<ShadowsocksController.TrafficPerSecond> snapshot = controller.GetTrafficSnapshot(TrafficWindowSeconds);
        long[] inbound = snapshot.Select(item => Math.Max(0, item.InboundIncrement)).ToArray();
        long[] outbound = snapshot.Select(item => Math.Max(0, item.OutboundIncrement)).ToArray();
        _ = DispatcherQueue.TryEnqueue(() => UpdateTrafficSamples(inbound, outbound));
    }

    private void RefreshTrafficSnapshot()
    {
        var controller = _context.Controller;
        if (controller is null)
        {
            UpdateTrafficSamples([], []);
            return;
        }

        IReadOnlyList<ShadowsocksController.TrafficPerSecond> snapshot = controller.GetTrafficSnapshot(TrafficWindowSeconds);
        UpdateTrafficSamples(
            snapshot.Select(item => Math.Max(0, item.InboundIncrement)).ToArray(),
            snapshot.Select(item => Math.Max(0, item.OutboundIncrement)).ToArray());
    }

    private void UpdateTrafficSamples(long[] inbound, long[] outbound)
    {
        _inboundSamples = inbound;
        _outboundSamples = outbound;
        UpdateTrafficText();
        RenderTrafficChart();
    }

    private void RenderTrafficChart()
    {
        int count = Math.Min(_inboundSamples.Length, _outboundSamples.Length);
        if (count == 0)
        {
            _inboundGraph.Text = _context.L("Inbound") + "  " + new string('▁', TrafficWindowSeconds);
            _outboundGraph.Text = _context.L("Outbound") + " " + new string('▁', TrafficWindowSeconds);
            _chartScale.Text = _context.L("Scale 1 KiB/s");
            _chartCurrent.Text = _context.L("In 0 B/s · Out 0 B/s");
            return;
        }

        long maximum = 1024;
        for (int index = 0; index < count; index++)
        {
            maximum = Math.Max(maximum, Math.Max(_inboundSamples[index], _outboundSamples[index]));
        }

        _inboundGraph.Text = _context.L("Inbound") + "  " + BuildSparkline(_inboundSamples, maximum);
        _outboundGraph.Text = _context.L("Outbound") + " " + BuildSparkline(_outboundSamples, maximum);
        _chartScale.Text = _context.LF("Scale {0}/s", FormatBytes(maximum));
        _chartCurrent.Text = _context.LF("In {0}/s · Out {1}/s", FormatBytes(_inboundSamples[count - 1]), FormatBytes(_outboundSamples[count - 1]));
    }

    private static string BuildSparkline(long[] samples, long maximum)
    {
        var builder = new StringBuilder(TrafficWindowSeconds);
        int padding = Math.Max(0, TrafficWindowSeconds - samples.Length);
        builder.Append('▁', padding);

        foreach (long sample in samples.TakeLast(TrafficWindowSeconds))
        {
            double normalized = maximum <= 0 ? 0 : Math.Clamp(sample / (double)maximum, 0, 1);
            int level = (int)Math.Round(normalized * (SparkLevels.Length - 1));
            builder.Append(SparkLevels[level]);
        }

        return builder.ToString();
    }

    private void OnViewerOptionChanged(object _, RoutedEventArgs _1)
    {
        ApplyWrapMode();
        _toolbar.Visibility = Visibility.Visible;
        _context.SetAlwaysOnTop(_topMost.IsChecked == true);

        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        LogViewerConfig config = GetLogViewerConfig();
        config.wrapText = _wrapText.IsChecked == true;
        config.topMost = _topMost.IsChecked == true;
        config.toolbarShown = true;
        _pendingViewerConfig = config;
        _viewerConfigSaveTimer.Stop();
        _viewerConfigSaveTimer.Start();
    }

    private void ApplyViewerConfig(LogViewerConfig config)
    {
        string viewerFontFamily = string.IsNullOrWhiteSpace(config.fontFamily) ? "Consolas" : config.fontFamily;
        double viewerFontSize = PointsToDip(config.fontSize > 0 ? config.fontSize : 8F);
        int viewerFontStyle = config.fontStyle;
        _viewer.FontFamily = new FontFamily(viewerFontFamily);
        _viewer.FontSize = viewerFontSize;
        var weight = _viewer.FontWeight;
        weight.Weight = (ushort)(((viewerFontStyle & FontStyleBold) != 0) ? 700 : 400);
        _viewer.FontWeight = weight;
        Type fontStyleType = _viewer.FontStyle.GetType();
        _viewer.SetValue(
            RichTextBlock.FontStyleProperty,
            Enum.Parse(fontStyleType, (viewerFontStyle & FontStyleItalic) != 0 ? "Italic" : "Normal", ignoreCase: false));
        ApplyWrapMode();
        _toolbar.Visibility = Visibility.Visible;
    }

    private LogViewerConfig GetLogViewerConfig()
        => _context.Controller?.GetCurrentConfiguration().logViewer ?? new LogViewerConfig();

    private void ApplyWrapMode()
    {
        bool viewerWrap = _wrapText.IsChecked == true;
        _viewer.TextWrapping = viewerWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _viewerScroll.HorizontalScrollMode = viewerWrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        _viewerScroll.HorizontalScrollBarVisibility = viewerWrap
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private void FlushPendingViewerConfig()
    {
        LogViewerConfig? config = _pendingViewerConfig;
        _pendingViewerConfig = null;
        if (config is null || _context.Controller is null)
            return;

        _ = PersistViewerConfigAsync(config);
    }

    private async Task PersistViewerConfigAsync(LogViewerConfig config)
    {
        if (_disposed)
            return;

        try
        {
            await Task.Run(() =>
            {
                lock (_loggingSettingsGate)
                {
                    _context.Controller?.SaveLogViewerConfig(config);
                }
            }).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Logs", _context.LF("Unable to save log viewer settings: {0}", exception.Message), InfoBarSeverity.Error);
        }
    }

    private void UpdateTimer()
    {
        if (_autoRefresh.IsOn && IsLoaded)
        {
            if (!_timer.IsRunning)
            {
                _timer.Start();
            }
        }
        else
        {
            _timer.Stop();
        }
    }

    private void QueueLogRefresh(bool force = false)
    {
        if (!IsLoaded && !force)
            return;

        int generation = Interlocked.Increment(ref _logRefreshGeneration);
        _cancelLogRefresh?.Invoke();
        _ = RefreshLogTextAsync(force, generation);
    }

    private void CancelLogRefresh()
    {
        Interlocked.Increment(ref _logRefreshGeneration);
        _cancelLogRefresh?.Invoke();
        _cancelLogRefresh = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        _viewerConfigSaveTimer.Stop();
        _pendingViewerConfig = null;
        CancelLogRefresh();
        UnsubscribeTraffic();
        GC.SuppressFinalize(this);
    }

    private async Task RefreshLogTextAsync(bool force, int generation)
    {
        using var cancellation = new CancellationTokenSource();
        Action cancel = cancellation.Cancel;
        _cancelLogRefresh = cancel;
        CancellationToken cancellationToken = cancellation.Token;
        UpdateTrafficText();
        string path = LoggingConfigurator.LogFilePath;
        _logPath.Text = path;
        string truncatedMarker = _context.L("… showing the last 256 KiB …");
        long previousLength = _lastRenderedLogLength;
        DateTime previousWriteUtc = _lastRenderedLogWriteUtc;

        try
        {
            LogTailSnapshot snapshot = await Task.Run(
                () => ReadLogTail(path, previousLength, previousWriteUtc, truncatedMarker, force, cancellationToken),
                cancellationToken).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested || generation != _logRefreshGeneration)
                return;

            if (!snapshot.Exists)
            {
                SetViewerMessage(_context.L("Log file has not been created yet."));
                _lastRenderedLogLength = -1;
                _lastRenderedLogWriteUtc = DateTime.MinValue;
                return;
            }

            if (snapshot.Unchanged)
                return;

            PopulateViewer(snapshot.Text);
            _lastRenderedLogLength = snapshot.Length;
            _lastRenderedLogWriteUtc = snapshot.LastWriteUtc;
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this one.
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested && generation == _logRefreshGeneration)
                SetViewerMessage(_context.LF("Unable to read log: {0}", exception.Message), LogLineSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_cancelLogRefresh, cancel))
                _cancelLogRefresh = null;
        }
    }

    private static LogTailSnapshot ReadLogTail(
        string path,
        long previousLength,
        DateTime previousWriteUtc,
        string truncatedMarker,
        bool force,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return LogTailSnapshot.Missing;

        var fileInfo = new FileInfo(path);
        if (!force && fileInfo.Length == previousLength && fileInfo.LastWriteTimeUtc == previousWriteUtc)
            return new LogTailSnapshot(true, true, fileInfo.Length, fileInfo.LastWriteTimeUtc, string.Empty);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long offset = Math.Max(0, stream.Length - MaxTailBytes);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        cancellationToken.ThrowIfCancellationRequested();

        if (offset > 0)
        {
            int newline = text.IndexOf('\n');
            if (newline >= 0 && newline + 1 < text.Length)
                text = text[(newline + 1)..];
            text = truncatedMarker + "\r\n" + text;
        }

        text = TrimToLastLines(text, MaxVisibleLogLines);
        return new LogTailSnapshot(true, false, fileInfo.Length, fileInfo.LastWriteTimeUtc, text);
    }

    private static string TrimToLastLines(string text, int maxLines)
    {
        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        int firstLine = Math.Max(0, lines.Length - maxLines);
        string visibleText = string.Join(Environment.NewLine, lines, firstLine, lines.Length - firstLine);
        return visibleText.EndsWith(Environment.NewLine, StringComparison.Ordinal)
            ? visibleText[..^Environment.NewLine.Length]
            : visibleText;
    }

    private void PopulateViewer(string text)
    {
        _lastViewerText = text;
        _lastViewerIsStandaloneMessage = false;
        SetViewerText(FilterVisibleLogLines(text));
    }

    private void SetViewerMessage(string message, LogLineSeverity severity = LogLineSeverity.Normal)
    {
        _lastViewerText = message;
        _lastViewerIsStandaloneMessage = true;
        SetViewerText(message);
    }

    private void RenderCurrentViewerText()
    {
        SetViewerText(_lastViewerIsStandaloneMessage
            ? _lastViewerText
            : FilterVisibleLogLines(_lastViewerText));
    }

    private string FilterVisibleLogLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        bool showVerbose = _verboseToggle.IsOn;
        bool showPluginOutput = _pluginOutputToggle.IsOn;
        bool showDnsLogs = _dnsLogsToggle.IsOn;
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var visible = new StringBuilder(normalized.Length);
        bool includeContinuation = true;

        foreach (string line in lines)
        {
            string[] parts = line.Split('|', 4);
            if (parts.Length >= 4)
            {
                string level = parts[1]?.Trim() ?? string.Empty;
                string loggerName = parts[2]?.Trim() ?? string.Empty;
                string message = parts[3] ?? string.Empty;
                bool verboseLine = level.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
                    || level.Equals("TRACE", StringComparison.OrdinalIgnoreCase);
                bool pluginLine = message.Contains("SIP003 |", StringComparison.OrdinalIgnoreCase)
                    || loggerName.EndsWith(".Sip003Plugin", StringComparison.OrdinalIgnoreCase);
                bool dnsDiagnosticLine = message.Contains("DNSCryptProxy |", StringComparison.OrdinalIgnoreCase)
                    || loggerName.Contains("DnsCryptRuntimeManager", StringComparison.OrdinalIgnoreCase)
                    || loggerName.Contains("TransparentDohRelay", StringComparison.OrdinalIgnoreCase);
                includeContinuation = (!verboseLine || showVerbose)
                    && (!pluginLine || showPluginOutput)
                    && (!dnsDiagnosticLine || showDnsLogs);
            }

            if (!includeContinuation)
                continue;
            if (visible.Length > 0)
                visible.AppendLine();
            visible.Append(line);
        }

        return visible.ToString();
    }

    private void SetViewerText(string text)
    {
        bool wasAtBottom = _viewerScroll.ScrollableHeight <= 0
            || _viewerScroll.VerticalOffset >= _viewerScroll.ScrollableHeight - 24;

        _viewer.Blocks.Clear();
        var paragraph = new Paragraph();
        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        Brush? secondary = TryGetThemeBrush("TextFillColorSecondaryBrush");
        Brush? warning = TryGetThemeBrush("SystemFillColorCautionBrush");
        Brush? error = TryGetThemeBrush("SystemFillColorCriticalBrush");
        LogLineSeverity inheritedSeverity = LogLineSeverity.Normal;
        for (int index = 0; index < lines.Length; index++)
        {
            inheritedSeverity = AppendStyledLogLine(paragraph, lines[index], inheritedSeverity, secondary, warning, error);
            if (index < lines.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }
        _viewer.Blocks.Add(paragraph);

        if (wasAtBottom)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
                _viewerScroll.ChangeView(null, _viewerScroll.ScrollableHeight, null, disableAnimation: true));
        }
    }

    private static LogLineSeverity AppendStyledLogLine(
        Paragraph paragraph,
        string line,
        LogLineSeverity inheritedSeverity,
        Brush? secondary,
        Brush? warning,
        Brush? error)
    {
        if (string.IsNullOrEmpty(line))
            return inheritedSeverity;

        string[] parts = line.Split('|', 4);
        if (parts.Length < 4)
        {
            Brush? continuationBrush = inheritedSeverity switch
            {
                LogLineSeverity.Error => error,
                LogLineSeverity.Warning => warning,
                _ => secondary,
            };
            paragraph.Inlines.Add(CreateRun("  " + line, continuationBrush));
            return inheritedSeverity;
        }

        LogLineSeverity severity = GetSeverity(parts[1]);
        string level = parts[1]?.Trim() ?? string.Empty;
        Brush? lineBrush = severity switch
        {
            LogLineSeverity.Error => error,
            LogLineSeverity.Warning => warning,
            _ when level.Equals("DEBUG", StringComparison.OrdinalIgnoreCase)
                || level.Equals("TRACE", StringComparison.OrdinalIgnoreCase) => secondary,
            _ => null,
        };
        paragraph.Inlines.Add(CreateRun(line, lineBrush, bold: severity != LogLineSeverity.Normal));
        return severity;
    }

    private static Run CreateRun(string text, Brush? foreground, bool bold = false)
    {
        var run = new Run { Text = text };
        if (foreground is not null)
            run.Foreground = foreground;
        if (bold)
        {
            var weight = run.FontWeight;
            weight.Weight = 700;
            run.FontWeight = weight;
        }
        return run;
    }

    private static LogLineSeverity GetSeverity(string level)
    {
        string value = level?.Trim() ?? string.Empty;
        if (value.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || value.Equals("FATAL", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase))
        {
            return LogLineSeverity.Error;
        }
        if (value.Equals("WARN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return LogLineSeverity.Warning;
        }
        return LogLineSeverity.Normal;
    }

    private static Brush? TryGetThemeBrush(string key)
    {
        ResourceDictionary? resources = Application.Current?.Resources;
        if (resources is null)
            return null;
        try
        {
            return resources[key] as Brush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ClearLogs()
    {
        try
        {
            string path = LoggingConfigurator.LogFilePath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            _viewer.Blocks.Clear();
            _lastViewerText = string.Empty;
            _lastViewerIsStandaloneMessage = false;
            _lastRenderedLogLength = -1;
            _lastRenderedLogWriteUtc = DateTime.MinValue;
            _context.ShowInfo("Logs", "Log file cleared.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Logs", exception.Message, InfoBarSeverity.Error);
        }
    }

    private void UpdateTrafficText()
    {
        if (_context.Controller is null)
        {
            _traffic.Text = _context.L("Traffic unavailable");
            return;
        }
        _traffic.Text = _context.LF("In: {0} · Out: {1}", FormatBytes(_context.Controller.InboundCounter), FormatBytes(_context.Controller.OutboundCounter));
    }

    private static double PointsToDip(float points) => points * 96.0 / 72.0;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _context.ShowInfo("Logs", exception.Message, InfoBarSeverity.Error);
        }
    }
    private sealed record LogTailSnapshot(bool Exists, bool Unchanged, long Length, DateTime LastWriteUtc, string Text)
    {
        public static LogTailSnapshot Missing { get; } = new(false, false, -1, DateTime.MinValue, string.Empty);
    }

}
