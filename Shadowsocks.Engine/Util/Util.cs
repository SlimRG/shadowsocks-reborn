using System;
using System.IO;
using System.IO.Compression;
using Microsoft.Win32;
using NLog;
using Shadowsocks.Controller;
using Shadowsocks.Model;

namespace Shadowsocks.Util
{
    public struct BandwidthScaleInfo
    {
        public float value;
        public string unitName;
        public long unit;

        public BandwidthScaleInfo(float value, string unitName, long unit)
        {
            this.value = value;
            this.unitName = unitName;
            this.unit = unit;
        }
    }

    public static class Utils
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private static string _tempPath = null;

        // return path to store temporary files
        public static string GetTempPath()
        {
            if (_tempPath == null)
            {
                bool isPortableMode = Configuration.Load().portableMode;
                try
                {
                    if (isPortableMode)
                    {
                        _tempPath = Directory.CreateDirectory("ss_win_temp").FullName;
                        // don't use "/", it will fail when we call explorer /select xxx/ss_win_temp\xxx.log
                    }
                    else
                    {
                        _tempPath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), @"Shadowsocks\ss_win_temp_" + GetDeterministicHashCode(Shadowsocks.Engine.RuntimeEnvironment.ExecutablePath))).FullName;
                    }
                }
                catch (Exception e)
                {
                    logger.Error(e);
                    throw;
                }
            }
            return _tempPath;
        }

        // .NET Framework String.GetHashCode() was stable between processes. Modern .NET
        // randomizes string hashes, so process-wide identifiers must not use string.GetHashCode().
        // This reproduces the legacy algorithm used by the original application.
        public static int GetDeterministicHashCode(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));

            unchecked
            {
                int hash1 = (5381 << 16) + 5381;
                int hash2 = hash1;
                int i = 0;

                while (i < value.Length)
                {
                    int block = value[i];
                    if (i + 1 < value.Length) block |= value[i + 1] << 16;
                    hash1 = ((hash1 << 5) + hash1 + (hash1 >> 27)) ^ block;
                    i += 2;
                    if (i >= value.Length) break;

                    block = value[i];
                    if (i + 1 < value.Length) block |= value[i + 1] << 16;
                    hash2 = ((hash2 << 5) + hash2 + (hash2 >> 27)) ^ block;
                    i += 2;
                }

                return hash1 + hash2 * 1566083941;
            }
        }

        public enum WindowsThemeMode { Dark, Light }

        // Support on Windows 10 1903+
        public static WindowsThemeMode GetWindows10SystemThemeSetting()
        {
            WindowsThemeMode themeMode = WindowsThemeMode.Dark;
            try
            {
                RegistryKey reg_ThemesPersonalize = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
                if (reg_ThemesPersonalize.GetValue("SystemUsesLightTheme") != null)
                {
                    if ((int)(reg_ThemesPersonalize.GetValue("SystemUsesLightTheme")) == 0) // 0:dark mode, 1:light mode
                        themeMode = WindowsThemeMode.Dark;
                    else
                        themeMode = WindowsThemeMode.Light;
                }
                else
                {
                    throw new Exception("Reg-Value SystemUsesLightTheme not found.");
                }
            }
            catch
            {

                logger.Debug(
                        $"Cannot get Windows 10 system theme mode, return default value 0 (dark mode).");

            }
            return themeMode;
        }

        // return a full path with filename combined which pointed to the temporary directory
        public static string GetTempPath(string filename)
        {
            return Path.Combine(GetTempPath(), filename);
        }

        public static string UnGzip(byte[] buf)
        {
            byte[] buffer = new byte[1024];
            int n;
            using (MemoryStream sb = new MemoryStream())
            {
                using (GZipStream input = new GZipStream(new MemoryStream(buf),
                                                         CompressionMode.Decompress,
                                                         false))
                {
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sb.Write(buffer, 0, n);
                    }
                }
                return System.Text.Encoding.UTF8.GetString(sb.ToArray());
            }
        }

        public static string FormatBandwidth(long n)
        {
            var result = GetBandwidthScale(n);
            return $"{result.value:0.##}{result.unitName}";
        }

        public static string FormatBytes(long bytes)
        {
            const long K = 1024L;
            const long M = K * 1024L;
            const long G = M * 1024L;
            const long T = G * 1024L;
            const long P = T * 1024L;
            const long E = P * 1024L;

            if (bytes >= P * 990)
                return (bytes / (double)E).ToString("F5") + "EiB";
            if (bytes >= T * 990)
                return (bytes / (double)P).ToString("F5") + "PiB";
            if (bytes >= G * 990)
                return (bytes / (double)T).ToString("F5") + "TiB";
            if (bytes >= M * 990)
            {
                return (bytes / (double)G).ToString("F4") + "GiB";
            }
            if (bytes >= M * 100)
            {
                return (bytes / (double)M).ToString("F1") + "MiB";
            }
            if (bytes >= M * 10)
            {
                return (bytes / (double)M).ToString("F2") + "MiB";
            }
            if (bytes >= K * 990)
            {
                return (bytes / (double)M).ToString("F3") + "MiB";
            }
            if (bytes > K * 2)
            {
                return (bytes / (double)K).ToString("F1") + "KiB";
            }
            return bytes.ToString() + "B";
        }

        /// <summary>
        /// Return scaled bandwidth
        /// </summary>
        /// <param name="n">Raw bandwidth</param>
        /// <returns>
        /// The BandwidthScaleInfo struct
        /// </returns>
        public static BandwidthScaleInfo GetBandwidthScale(long n)
        {
            long scale = 1;
            float f = n;
            string unit = "B";
            if (f > 1024)
            {
                f = f / 1024;
                scale <<= 10;
                unit = "KiB";
            }
            if (f > 1024)
            {
                f = f / 1024;
                scale <<= 10;
                unit = "MiB";
            }
            if (f > 1024)
            {
                f = f / 1024;
                scale <<= 10;
                unit = "GiB";
            }
            if (f > 1024)
            {
                f = f / 1024;
                scale <<= 10;
                unit = "TiB";
            }
            return new BandwidthScaleInfo(f, unit, scale);
        }

        public static RegistryKey OpenRegKey(string name, bool writable, RegistryHive hive = RegistryHive.CurrentUser)
        {
            // The client is x64-only. Select the registry view from the OS so this helper
            // remains explicit and safe on supported 64-bit Windows systems.
            if (string.IsNullOrEmpty(name)) throw new ArgumentException(nameof(name));
            try
            {
                RegistryKey userKey = RegistryKey.OpenBaseKey(hive,
                        Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32)
                    .OpenSubKey(name, writable);
                return userKey;
            }
            catch (ArgumentException ae)
            {
                logger.LogUsefulException(ae);
                return null;
            }
            catch (Exception e)
            {
                logger.LogUsefulException(e);
                return null;
            }
        }

    }
}
