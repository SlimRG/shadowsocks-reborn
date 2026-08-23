using System;
using System.Text.Json;

namespace Shadowsocks.Core.Settings;

public static class AppSettings
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<AppSettingsDocument> s_settings = new(Load);

    public static LoggingSettings Logging => s_settings.Value.Logging;

    private static AppSettingsDocument Load()
    {
        try
        {
            string json = EmbeddedResources.AppSettingsJson;
            return JsonSerializer.Deserialize<AppSettingsDocument>(json, s_jsonOptions) ?? new AppSettingsDocument();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The embedded appsettings.json is invalid.", exception);
        }
    }
}

public sealed class LoggingSettings
{
    public string FilePath { get; set; } = "shadowsocks.log";

    public string FallbackFilePath { get; set; } = "shadowsocks.log";

    public string MinimumLevel { get; set; } = "Info";

    public string VerboseMinimumLevel { get; set; } = "Debug";

    public long ArchiveAboveSizeBytes { get; set; } = 10L * 1024 * 1024;

    public int MaxArchiveFiles { get; set; } = 7;

    public int MaxArchiveDays { get; set; } = 7;

    public string Layout { get; set; } =
        "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:inner=${newline}${exception:format=tostring}}";
}

public sealed class AppSettingsDocument
{
    public LoggingSettings Logging { get; set; } = new();
}
