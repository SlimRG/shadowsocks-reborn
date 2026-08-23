using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Shadowsocks.WinUI.UI;

internal static class WinUIStyles
{
    public const double PageGutter = 16;
    public const double PageTopGutter = 16;
    public const double CardPadding = 16;

    public static ScrollViewer CreatePage(string title, string subtitle, out StackPanel panel)
    {
        panel = new StackPanel
        {
            Spacing = 20,
            Margin = new Thickness(0, PageTopGutter, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(CreateText(title, "TitleTextBlockStyle"));
        var subtitleBlock = CreateText(subtitle, "BodyTextBlockStyle");
        subtitleBlock.Opacity = 0.72;
        subtitleBlock.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(subtitleBlock);
        panel.Children.Add(header);

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
    }

    public static Border CreateCard(UIElement content)
    {
        return new Border
        {
            Background = GetBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = GetBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(CardPadding),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
    }

    public static TextBlock CreateText(string text, string styleKey = "BodyTextBlockStyle")
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        ApplyStyle(textBlock, styleKey);
        return textBlock;
    }

    public static TextBlock CreateSectionTitle(string text)
    {
        return CreateText(text, "BodyStrongTextBlockStyle");
    }

    public static FontIcon CreateWindows10Icon(string glyph, double fontSize = 16)
    {
        return new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = glyph,
            FontSize = fontSize,
        };
    }


    public static Grid CreateKeyValueRow(string label, out TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = CreateText(label, "BodyStrongTextBlockStyle");
        labelBlock.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(labelBlock);

        value = CreateText("—");
        value.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }

    public static Brush? GetBrush(string key)
    {
        try
        {
            return Application.Current.Resources[key] as Brush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ApplyStyle(FrameworkElement element, string key)
    {
        try
        {
            if (Application.Current.Resources[key] is Style style)
            {
                element.Style = style;
            }
        }
        catch (Exception)
        {
            // The built-in type ramp is preferred, but the controls retain accessible defaults
            // if a resource is unavailable on an older/fallback Windows environment.
        }
    }
}
