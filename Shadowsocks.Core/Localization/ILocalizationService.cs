using System.Globalization;

namespace Shadowsocks.Localization;

public interface ILocalizationService
{
    CultureInfo Culture { get; }

    string this[string key] { get; }

    string Format(string key, params object[] args);
}
