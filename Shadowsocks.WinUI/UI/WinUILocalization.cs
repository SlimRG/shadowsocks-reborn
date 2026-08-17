using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shadowsocks.Localization;

namespace Shadowsocks.WinUI.UI;

internal static class WinUILocalization
{
    public static void Apply(DependencyObject? root, ILocalizationService localization)
    {
        if (root is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(localization);
        LocalizeObject(root, localization);
        VisitChildren(root, localization);
    }

    private static void LocalizeObject(DependencyObject element, ILocalizationService localization)
    {
        switch (element)
        {
            case TextBlock textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                textBlock.Text = localization[textBlock.Text];
                break;

            case ToggleSwitch toggleSwitch:
                toggleSwitch.Header = LocalizeObjectValue(toggleSwitch.Header, localization);
                toggleSwitch.OnContent = LocalizeObjectValue(toggleSwitch.OnContent, localization);
                toggleSwitch.OffContent = LocalizeObjectValue(toggleSwitch.OffContent, localization);
                break;

            case TextBox textBox:
                textBox.Header = LocalizeObjectValue(textBox.Header, localization);
                if (!string.IsNullOrWhiteSpace(textBox.PlaceholderText))
                {
                    textBox.PlaceholderText = localization[textBox.PlaceholderText];
                }
                break;

            case PasswordBox passwordBox:
                passwordBox.Header = LocalizeObjectValue(passwordBox.Header, localization);
                if (!string.IsNullOrWhiteSpace(passwordBox.PlaceholderText))
                {
                    passwordBox.PlaceholderText = localization[passwordBox.PlaceholderText];
                }
                break;

            case NumberBox numberBox:
                numberBox.Header = LocalizeObjectValue(numberBox.Header, localization);
                if (!string.IsNullOrWhiteSpace(numberBox.PlaceholderText))
                {
                    numberBox.PlaceholderText = localization[numberBox.PlaceholderText];
                }
                break;

            case ComboBox comboBox:
                comboBox.Header = LocalizeObjectValue(comboBox.Header, localization);
                if (!string.IsNullOrWhiteSpace(comboBox.PlaceholderText))
                {
                    comboBox.PlaceholderText = localization[comboBox.PlaceholderText];
                }
                break;

            case InfoBar infoBar:
                if (!string.IsNullOrWhiteSpace(infoBar.Title))
                {
                    infoBar.Title = localization[infoBar.Title];
                }
                if (!string.IsNullOrWhiteSpace(infoBar.Message))
                {
                    infoBar.Message = localization[infoBar.Message];
                }
                break;

            case TeachingTip teachingTip:
                if (!string.IsNullOrWhiteSpace(teachingTip.Title))
                {
                    teachingTip.Title = localization[teachingTip.Title];
                }
                if (!string.IsNullOrWhiteSpace(teachingTip.Subtitle))
                {
                    teachingTip.Subtitle = localization[teachingTip.Subtitle];
                }
                break;

            case ContentControl contentControl:
                contentControl.Content = LocalizeObjectValue(contentControl.Content, localization);
                break;
        }
    }

    private static object? LocalizeObjectValue(object? value, ILocalizationService localization)
        => value is string text && !string.IsNullOrWhiteSpace(text) ? localization[text] : value;

    private static void VisitChildren(DependencyObject element, ILocalizationService localization)
    {
        switch (element)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    Apply(child, localization);
                }
                break;

            case Border border:
                Apply(border.Child, localization);
                break;

            case ScrollViewer scrollViewer when scrollViewer.Content is DependencyObject scrollContent:
                Apply(scrollContent, localization);
                break;

            case ContentControl contentControl when contentControl.Content is DependencyObject content:
                Apply(content, localization);
                break;

            case ItemsControl itemsControl:
                foreach (object item in itemsControl.Items)
                {
                    if (item is DependencyObject dependencyObject)
                    {
                        Apply(dependencyObject, localization);
                    }
                }
                break;
        }
    }
}
