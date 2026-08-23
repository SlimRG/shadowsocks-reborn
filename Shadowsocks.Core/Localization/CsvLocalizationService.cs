#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.VisualBasic.FileIO;
using NLog;

namespace Shadowsocks.Localization;

public sealed class CsvLocalizationService : ILocalizationService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

    public CsvLocalizationService(string csv, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(csv);
        Culture = culture ?? CultureInfo.CurrentCulture;
        Load(csv, Culture.Name);
    }

    public CultureInfo Culture { get; }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
            {
                return key ?? string.Empty;
            }

            string normalized = key.Trim();
            return _strings.TryGetValue(normalized, out string? value) ? value : key;
        }
    }

    public string Format(string key, params object[] args)
        => string.Format(Culture, this[key], args ?? Array.Empty<object>());

    public static CsvLocalizationService CreateDefault(CultureInfo? culture = null)
    {
        CultureInfo effectiveCulture = culture ?? CultureInfo.CurrentCulture;
        Logger.Info("Current language is: {0}", effectiveCulture.Name);
        return new CsvLocalizationService(Shadowsocks.Core.EmbeddedResources.I18nCsv, effectiveCulture);
    }

    private void Load(string csv, string locale)
    {
        using var parser = new TextFieldParser(new StringReader(csv));
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;

        string[]? localeNames = parser.ReadFields();
        if (localeNames is null || localeNames.Length == 0)
        {
            return;
        }

        int enIndex = Array.FindIndex(localeNames, value => string.Equals(value, "en", StringComparison.OrdinalIgnoreCase));
        if (enIndex < 0)
        {
            enIndex = 0;
        }

        int targetIndex = Array.FindIndex(localeNames, value => string.Equals(value, locale, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
        {
            string language = locale.Split('-')[0];
            targetIndex = Array.FindIndex(localeNames, value =>
                string.Equals(value.Split('-')[0], language, StringComparison.OrdinalIgnoreCase));

            if (targetIndex >= 0 && targetIndex != enIndex)
            {
                Logger.Info("Using {0} translation for {1}", localeNames[targetIndex], locale);
            }
            else if (targetIndex < 0)
            {
                Logger.Info("Translation for {0} not found; English keys will be used.", locale);
                return;
            }
        }

        while (!parser.EndOfData)
        {
            string[]? row = parser.ReadFields();
            if (row is null || row.Length <= enIndex)
            {
                continue;
            }

            string source = row[enIndex].Trim();
            if (string.IsNullOrWhiteSpace(source) || source.StartsWith('#'))
            {
                continue;
            }

            if (row.Length <= targetIndex)
            {
                continue;
            }

            string translation = row[targetIndex].Trim();
            if (!string.IsNullOrWhiteSpace(translation))
            {
                _strings[source] = translation;
            }
        }
    }
}
