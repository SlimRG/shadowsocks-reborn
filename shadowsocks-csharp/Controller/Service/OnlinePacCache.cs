using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller
{
    /// <summary>
    /// Persistent cache for a user-supplied online PAC file.
    /// Windows never fetches the remote PAC URL directly: PACServer serves this cache
    /// from localhost while refreshes are explicitly downloaded through Shadowsocks.
    /// </summary>
    internal static class OnlinePacCache
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();
        private static readonly SemaphoreSlim refreshLock = new SemaphoreSlim(1, 1);

        private static readonly string CachePath = Path.Combine(Program.WorkingDirectory, "online-pac-cache.pac");
        private static readonly string MetadataPath = Path.Combine(Program.WorkingDirectory, "online-pac-cache.meta.json");

        private const long MaxPacSize = 32L * 1024L * 1024L;

        private sealed class CacheMetadata
        {
            public string SourceUrl { get; set; } = "";
            public string ETag { get; set; } = "";
            public DateTimeOffset? LastModified { get; set; }
            public DateTimeOffset UpdatedUtc { get; set; }
        }

        public static bool TryGetContent(string sourceUrl, out string content)
        {
            content = null;
            if (string.IsNullOrWhiteSpace(sourceUrl) || !File.Exists(CachePath))
                return false;

            try
            {
                CacheMetadata metadata = ReadMetadata();
                if (metadata == null || !string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.Ordinal))
                    return false;

                string cached = File.ReadAllText(CachePath, Encoding.UTF8);
                if (!IsValidPac(cached))
                    return false;

                content = cached;
                return true;
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
                return false;
            }
        }

        public static async Task<bool> RefreshAsync(Configuration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(config.pacUrl))
                throw new InvalidOperationException("Online PAC URL is empty.");

            await refreshLock.WaitAsync().ConfigureAwait(false);
            try
            {
                CacheMetadata oldMetadata = ReadMetadata();
                bool sameSource = TryGetContent(config.pacUrl, out _);

                using HttpClient client = LocalProxyHttpClient.Create(config);
                using var request = new HttpRequestMessage(HttpMethod.Get, config.pacUrl);

                if (sameSource)
                {
                    if (!string.IsNullOrWhiteSpace(oldMetadata.ETag))
                        request.Headers.TryAddWithoutValidation("If-None-Match", oldMetadata.ETag);
                    if (oldMetadata.LastModified.HasValue)
                        request.Headers.IfModifiedSince = oldMetadata.LastModified;
                }

                logger.Info($"Refreshing online PAC through Shadowsocks: {config.pacUrl}");
                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && sameSource)
                {
                    logger.Info("Online PAC cache is up to date (HTTP 304).");
                    return false;
                }

                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxPacSize)
                    throw new InvalidDataException($"Online PAC is too large ({contentLength} bytes).");

                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes.LongLength > MaxPacSize)
                    throw new InvalidDataException($"Online PAC is too large ({bytes.LongLength} bytes).");

                string content = DecodePac(bytes, response.Content.Headers.ContentType?.CharSet);
                if (!IsValidPac(content))
                    throw new InvalidDataException("Downloaded online PAC does not contain FindProxyForURL().");

                string previousContent = null;
                if (sameSource)
                {
                    try
                    {
                        previousContent = File.ReadAllText(CachePath, Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        logger.LogUsefulException(ex);
                    }
                }

                WriteAtomic(CachePath, content);
                WriteMetadataAtomic(new CacheMetadata
                {
                    SourceUrl = config.pacUrl,
                    ETag = response.Headers.ETag?.ToString() ?? "",
                    LastModified = response.Content.Headers.LastModified,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });

                logger.Info("Online PAC cache refreshed successfully.");
                return !sameSource || !string.Equals(previousContent, content, StringComparison.Ordinal);
            }
            finally
            {
                refreshLock.Release();
            }
        }

        private static string DecodePac(byte[] bytes, string charset)
        {
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try
                {
                    return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    // Fall back to UTF-8 below.
                }
            }

            return Encoding.UTF8.GetString(bytes);
        }

        private static bool IsValidPac(string content)
            => !string.IsNullOrWhiteSpace(content) &&
               content.IndexOf("FindProxyForURL", StringComparison.Ordinal) >= 0;

        private static CacheMetadata ReadMetadata()
        {
            if (!File.Exists(MetadataPath))
                return null;

            try
            {
                return JsonConvert.DeserializeObject<CacheMetadata>(File.ReadAllText(MetadataPath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
                return null;
            }
        }

        private static void WriteMetadataAtomic(CacheMetadata metadata)
            => WriteAtomic(MetadataPath, JsonConvert.SerializeObject(metadata, Formatting.Indented));

        private static void WriteAtomic(string path, string content)
        {
            string tempPath = path + ".new";
            File.WriteAllText(tempPath, content, new UTF8Encoding(false));
            File.Move(tempPath, path, true);
        }
    }
}
