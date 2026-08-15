using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Shadowsocks.Engine
{
    public static class EmbeddedResources
    {
        public static string AbpJs => ReadText("Shadowsocks.Engine.Data.abp.js", Encoding.UTF8);
        public static string I18nCsv => ReadText("Shadowsocks.Engine.Data.i18n.csv", Encoding.UTF8);
        public static string NLogConfig => ReadText("Shadowsocks.Engine.Data.NLog.config", Encoding.UTF8);
        public static string UserRule => ReadText("Shadowsocks.Engine.Data.user-rule.txt", Encoding.UTF8);

        private static string ReadText(string resourceName, Encoding encoding)
        {
            Assembly assembly = typeof(EmbeddedResources).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
            using StreamReader reader = new(stream, encoding, true);
            return reader.ReadToEnd();
        }
    }
}
