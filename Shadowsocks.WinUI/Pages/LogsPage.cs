using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Core.Logging;
using Shadowsocks.Model;
using Shadowsocks.WinUI.UI;

namespace Shadowsocks.WinUI.Pages;

public sealed class LogsPage : Page, IRefreshablePage
{
    private const int MaxTailBytes = 512 * 1024;
    private const int MaxVisibleLogLines = 2000;
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
    private readonly ListView _viewer;
    private readonly ToggleSwitch _autoRefresh;
    private readonly ToggleSwitch _verboseToggle;
    private readonly ToggleSwitch _pluginOutputToggle;
    private readonly CheckBox _wrapText;
    private readonly CheckBox _topMost;
    private readonly DispatcherQueueTimer _timer;

    private long[] _inboundSamples = [];
    private long[] _outboundSamples = [];
    private bool _refreshing;
    private bool _trafficSubscribed;
    private long _lastRenderedLogLength = -1;
    private DateTime _lastRenderedLogWriteUtc = DateTime.MinValue;
    private string _viewerFontFamily = "Consolas";
    private double _viewerFontSize = PointsToDip(8F);
    private int _viewerFontStyle;
    private bool _viewerWrap;
    private string _lastViewerText = string.Empty;
    private LogLineSeverity _lastStandaloneSeverity = LogLineSeverity.Normal;
    private bool _lastViewerIsStandaloneMessage;

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
        refresh.Click += (_, _) => RefreshLogText();
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
        loggingOptions.Children.Add(loggingOptionsRow);
        panel.Children.Add(WinUIStyles.CreateCard(loggingOptions));

        var chart = new StackPanel { Spacing = 6 };
        chart.Children.Add(WinUIStyles.CreateSectionTitle("Traffic · last 60 seconds"));
        _chartCurrent = WinUIStyles.CreateText("In 0 B/s · Out 0 B/s", "CaptionTextBlockStyle");
        chart.Children.Add(_chartCurrent);
        _chartScale = WinUIStyles.CreateText("Scale 1 KiB/s", "CaptionTextBlockStyle");
        chart.Children.Add(_chartScale);
        _inboundGraph = CreateTrafficGraphText("Inbound  ");
        _outboundGraph = CreateTrafficGraphText("Outbound ");
        chart.Children.Add(_inboundGraph);
        chart.Children.Add(_outboundGraph);
        panel.Children.Add(WinUIStyles.CreateCard(chart));

        _viewer = new ListView
        {
            Height = LogViewerHeight,
            SelectionMode = ListViewSelectionMode.None,
            IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 6, 4, 6),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_viewer, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_viewer, ScrollBarVisibility.Auto);
        panel.Children.Add(WinUIStyles.CreateCard(_viewer));

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => RefreshLogText();
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
            LogViewerConfig config = configuration?.logViewer ?? new LogViewerConfig();
            _wrapText.IsChecked = config.wrapText;
            _topMost.IsChecked = config.topMost;
            _verboseToggle.IsOn = configuration?.isVerboseLogging == true;
            _pluginOutputToggle.IsOn = configuration?.showPluginOutput == true;
            ApplyViewerConfig(config);
            _context.SetAlwaysOnTop(config.topMost);
        }
        finally
        {
            _refreshing = false;
        }

        RefreshTrafficSnapshot();
        RefreshLogText();
        UpdateTimer();
    }

    private void OnVerboseToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleVerboseLogging(_verboseToggle.IsOn);
    }

    private void OnPluginOutputToggled(object _, RoutedEventArgs _1)
    {
        if (_refreshing || _context.Controller is null)
        {
            return;
        }

        _context.Controller.ToggleShowPluginOutput(_pluginOutputToggle.IsOn);
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
        UpdateTimer();
    }

    private void OnUnloaded(object _, RoutedEventArgs _1)
    {
        _timer.Stop();
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

        // TrafficChanged is raised by the statistics thread immediately after the queue is rotated,
        // so this snapshot is taken on the same thread before the next mutation.
        var snapshot = controller.trafficPerSecondQueue.TakeLast(TrafficWindowSeconds).ToArray();
        long[] inbound = snapshot.Select(item => Math.Max(0, item.inboundIncreasement)).ToArray();
        long[] outbound = snapshot.Select(item => Math.Max(0, item.outboundIncreasement)).ToArray();
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

        try
        {
            var snapshot = controller.trafficPerSecondQueue.TakeLast(TrafficWindowSeconds).ToArray();
            UpdateTrafficSamples(
                snapshot.Select(item => Math.Max(0, item.inboundIncreasement)).ToArray(),
                snapshot.Select(item => Math.Max(0, item.outboundIncreasement)).ToArray());
        }
        catch (InvalidOperationException)
        {
            // A manual refresh can race the statistics thread. The next TrafficChanged event
            // provides a stable snapshot from the statistics thread itself.
        }
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
        _context.Controller.SaveLogViewerConfig(config);
    }

    private void ApplyViewerConfig(LogViewerConfig config)
    {
        _viewerWrap = _wrapText.IsChecked == true;
        _viewerFontFamily = string.IsNullOrWhiteSpace(config.fontFamily) ? "Consolas" : config.fontFamily;
        _viewerFontSize = PointsToDip(config.fontSize > 0 ? config.fontSize : 8F);
        _viewerFontStyle = config.fontStyle;
        ApplyWrapMode();
        _toolbar.Visibility = Visibility.Visible;
        RebuildVisibleLogLines();
    }

    private LogViewerConfig GetLogViewerConfig()
        => _context.Controller?.GetCurrentConfiguration().logViewer ?? new LogViewerConfig();

    private void ApplyWrapMode()
    {
        _viewerWrap = _wrapText.IsChecked == true;
        ScrollViewer.SetHorizontalScrollBarVisibility(
            _viewer,
            _viewerWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        RebuildVisibleLogLines();
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

    private void RefreshLogText()
    {
        UpdateTrafficText();
        try
        {
            string path = LoggingConfigurator.LogFilePath;
            _logPath.Text = path;
            if (!File.Exists(path))
            {
                SetViewerMessage(_context.L("Log file has not been created yet."));
                return;
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length == _lastRenderedLogLength && fileInfo.LastWriteTimeUtc == _lastRenderedLogWriteUtc)
            {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long offset = Math.Max(0, stream.Length - MaxTailBytes);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string text = reader.ReadToEnd();
            if (offset > 0)
            {
                int newline = text.IndexOf('\n');
                if (newline >= 0 && newline + 1 < text.Length)
                {
                    text = text[(newline + 1)..];
                }
                text = _context.L("… showing the last 512 KiB …") + "\r\n" + text;
            }

            PopulateViewer(text);
            _lastRenderedLogLength = fileInfo.Length;
            _lastRenderedLogWriteUtc = fileInfo.LastWriteTimeUtc;
        }
        catch (Exception exception)
        {
            SetViewerMessage(_context.LF("Unable to read log: {0}", exception.Message), LogLineSeverity.Error);
        }
    }

    private void PopulateViewer(string text)
    {
        _lastViewerText = text;
        _lastViewerIsStandaloneMessage = false;

        string[] lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        int firstLine = Math.Max(0, lines.Length - MaxVisibleLogLines);
        _viewer.Items.Clear();

        LogLineSeverity severity = LogLineSeverity.Normal;
        for (int index = firstLine; index < lines.Length; index++)
        {
            string line = lines[index];
            if (index == lines.Length - 1 && line.Length == 0)
            {
                continue;
            }

            if (TryGetLogLineSeverity(line, out LogLineSeverity explicitSeverity))
            {
                severity = explicitSeverity;
            }

            _viewer.Items.Add(CreateLogLine(line, severity));
        }

        ScrollViewerToEnd();
    }

    private void RebuildVisibleLogLines()
    {
        if (_lastViewerIsStandaloneMessage)
        {
            SetViewerMessage(_lastViewerText, _lastStandaloneSeverity);
            return;
        }

        if (!string.IsNullOrEmpty(_lastViewerText))
        {
            PopulateViewer(_lastViewerText);
        }
    }

    private void SetViewerMessage(string message, LogLineSeverity severity = LogLineSeverity.Normal)
    {
        _lastViewerText = message;
        _lastStandaloneSeverity = severity;
        _lastViewerIsStandaloneMessage = true;
        _viewer.Items.Clear();
        _viewer.Items.Add(CreateLogLine(message, severity));
    }

    private FrameworkElement CreateLogLine(string text, LogLineSeverity severity)
    {
        bool structured = TryParseLogLine(text, out string timestamp, out string level, out string logger, out string message);

        var row = new Grid
        {
            ColumnSpacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (structured)
        {
            var timestampBlock = CreateViewerText(timestamp, severity: LogLineSeverity.Normal);
            timestampBlock.Opacity = 0.62;
            timestampBlock.FontSize = Math.Max(10, _viewerFontSize - 1);
            timestampBlock.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(timestampBlock);

            var levelBlock = CreateViewerText(level, severity);
            levelBlock.FontSize = Math.Max(10, _viewerFontSize - 1);
            levelBlock.HorizontalAlignment = HorizontalAlignment.Left;
            levelBlock.VerticalAlignment = VerticalAlignment.Top;
            var levelWeight = levelBlock.FontWeight;
            levelWeight.Weight = 600;
            levelBlock.FontWeight = levelWeight;

            var badge = new Border
            {
                Child = levelBlock,
                Padding = new Thickness(7, 1, 7, 2),
                CornerRadius = new CornerRadius(9),
                BorderThickness = new Thickness(1),
                BorderBrush = GetSeverityBrush(severity),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);

            var messagePanel = new StackPanel { Spacing = 2 };
            var messageBlock = CreateViewerText(message, severity);
            messageBlock.HorizontalAlignment = HorizontalAlignment.Stretch;
            messagePanel.Children.Add(messageBlock);

            if (!string.IsNullOrWhiteSpace(logger))
            {
                var loggerBlock = CreateViewerText(logger, LogLineSeverity.Normal);
                loggerBlock.Opacity = 0.52;
                loggerBlock.FontSize = Math.Max(9, _viewerFontSize - 2);
                loggerBlock.TextWrapping = TextWrapping.NoWrap;
                messagePanel.Children.Add(loggerBlock);
            }

            Grid.SetColumn(messagePanel, 2);
            row.Children.Add(messagePanel);
        }
        else
        {
            var continuation = CreateViewerText(text, severity);
            continuation.Opacity = string.IsNullOrWhiteSpace(text) ? 0.4 : 0.88;
            continuation.Margin = new Thickness(0, 0, 0, 0);
            Grid.SetColumn(continuation, 2);
            row.Children.Add(continuation);
        }

        return new Border
        {
            Child = row,
            Padding = new Thickness(10, structured ? 6 : 3, 10, structured ? 6 : 3),
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
    }

    private TextBlock CreateViewerText(string text, LogLineSeverity severity)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = _viewerWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(_viewerFontFamily),
            FontSize = _viewerFontSize,
            IsTextSelectionEnabled = true,
            HorizontalAlignment = _viewerWrap ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };

        var blockWeight = block.FontWeight;
        blockWeight.Weight = (ushort)(((_viewerFontStyle & FontStyleBold) != 0) ? 700 : 400);
        block.FontWeight = blockWeight;

        Type fontStyleType = block.FontStyle.GetType();
        object fontStyle = Enum.Parse(
            fontStyleType,
            (_viewerFontStyle & FontStyleItalic) != 0 ? "Italic" : "Normal",
            ignoreCase: false);
        block.SetValue(TextBlock.FontStyleProperty, fontStyle);

        var severityBrush = GetSeverityBrush(severity);
        if (severityBrush is not null && severity != LogLineSeverity.Normal)
        {
            block.Foreground = severityBrush;
        }

        return block;
    }

    private static Microsoft.UI.Xaml.Media.Brush? GetSeverityBrush(LogLineSeverity severity)
        => severity switch
        {
            LogLineSeverity.Error => WinUIStyles.GetBrush("SystemFillColorCriticalBrush"),
            LogLineSeverity.Warning => WinUIStyles.GetBrush("SystemFillColorCautionBrush"),
            _ => WinUIStyles.GetBrush("TextFillColorSecondaryBrush"),
        };

    private static bool TryParseLogLine(
        string line,
        out string timestamp,
        out string level,
        out string logger,
        out string message)
    {
        timestamp = string.Empty;
        level = string.Empty;
        logger = string.Empty;
        message = line;

        int first = line.IndexOf('|');
        if (first <= 0)
        {
            return false;
        }
        int second = line.IndexOf('|', first + 1);
        if (second <= first + 1)
        {
            return false;
        }
        int third = line.IndexOf('|', second + 1);
        if (third <= second + 1)
        {
            return false;
        }

        timestamp = CompactTimestamp(line[..first].Trim());
        level = line[(first + 1)..second].Trim().ToUpperInvariant();
        logger = line[(second + 1)..third].Trim();
        message = line[(third + 1)..].TrimEnd();
        return true;
    }

    private static string CompactTimestamp(string value)
    {
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            return parsed.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        }

        int space = value.LastIndexOf(' ');
        return space >= 0 && space + 1 < value.Length ? value[(space + 1)..] : value;
    }

    private static bool TryGetLogLineSeverity(string line, out LogLineSeverity severity)
    {
        severity = LogLineSeverity.Normal;
        int firstSeparator = line.IndexOf('|');
        if (firstSeparator < 0)
        {
            return false;
        }

        int secondSeparator = line.IndexOf('|', firstSeparator + 1);
        if (secondSeparator <= firstSeparator + 1)
        {
            return false;
        }

        string level = line[(firstSeparator + 1)..secondSeparator].Trim();
        switch (level.ToUpperInvariant())
        {
            case "WARN":
            case "WARNING":
                severity = LogLineSeverity.Warning;
                return true;
            case "ERROR":
            case "FATAL":
                severity = LogLineSeverity.Error;
                return true;
            case "TRACE":
            case "DEBUG":
            case "INFO":
                severity = LogLineSeverity.Normal;
                return true;
            default:
                return false;
        }
    }

    private void ScrollViewerToEnd()
    {
        if (_viewer.Items.Count > 0)
        {
            _viewer.ScrollIntoView(_viewer.Items[_viewer.Items.Count - 1]);
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
            _viewer.Items.Clear();
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
}
