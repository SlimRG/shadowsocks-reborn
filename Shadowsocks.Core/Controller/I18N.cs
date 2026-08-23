using System;
using Shadowsocks.Localization;

namespace Shadowsocks.Controller
{
    public static class I18N
    {

        private static ILocalizationService _service;

        public static ILocalizationService Service => _service ??= CsvLocalizationService.CreateDefault();

        public static void Configure(ILocalizationService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            _service = service;
        }

        public static string GetString(string key, params object[] args)
            => Service.Format(key, args);
    }
}
