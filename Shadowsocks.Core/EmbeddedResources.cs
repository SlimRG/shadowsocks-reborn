using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Shadowsocks.Core
{
    public static class EmbeddedResources
    {
        public static string AbpJs => ReadText("Shadowsocks.Core.Data.abp.js", Encoding.UTF8);
        public static string I18nCsv => ReadText("Shadowsocks.Core.Data.i18n.csv", Encoding.UTF8);
        public static string AppSettingsJson => ReadText("Shadowsocks.Core.appsettings.json", Encoding.UTF8);
        public static string UserRule => ReadText("Shadowsocks.Core.Data.user-rule.txt", Encoding.UTF8);
        public static string ProductLicense => ReadRequiredText("Shadowsocks.Core.LICENSE.txt", Encoding.UTF8);
        public static string ThirdPartyNotices => ReadRequiredText("Shadowsocks.Core.THIRD-PARTY-NOTICES.md", Encoding.UTF8);

        private static string ReadText(string resourceName, Encoding encoding)
        {
            Assembly assembly = typeof(EmbeddedResources).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
            using StreamReader reader = new(stream, encoding, true);
            return reader.ReadToEnd();
        }

        private static string ReadRequiredText(string resourceName, Encoding encoding)
        {
            string text = ReadText(resourceName, encoding);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException($"Embedded resource '{resourceName}' is empty.");
            }

            return text;
        }
    }
}
