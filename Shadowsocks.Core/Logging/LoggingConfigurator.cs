using System;
using System.IO;
using NLog;
using NLog.Config;
using NLog.Targets;
using Shadowsocks.Core.Settings;
using Shadowsocks.Core.Storage;

namespace Shadowsocks.Core.Logging;

public static class LoggingConfigurator
{
    private static readonly object SyncRoot = new();
    private static string activeLogFilePath;

    public static string LogFilePath => activeLogFilePath ?? ResolveLogFilePath(AppSettings.Logging.FilePath);

    public static void Configure(bool verbose = false)
    {
        lock (SyncRoot)
        {
            LoggingSettings settings = AppSettings.Logging;
            string logFilePath = PrepareLogFilePath(settings);
            activeLogFilePath = logFilePath;

            LogLevel minimumLevel = ParseLevel(
                verbose ? settings.VerboseMinimumLevel : settings.MinimumLevel,
                verbose ? LogLevel.Debug : LogLevel.Info);

            var fileTarget = new FileTarget("file")
            {
                FileName = logFilePath,
                Layout = settings.Layout,
                ArchiveAboveSize = Math.Max(1024L * 1024L, settings.ArchiveAboveSizeBytes),
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveSuffixFormat = "_{1:yyyyMMdd}_{0}",
                MaxArchiveFiles = Math.Max(1, settings.MaxArchiveFiles),
                MaxArchiveDays = Math.Max(1, settings.MaxArchiveDays),
            };

            var configuration = new LoggingConfiguration();
            configuration.AddRule(minimumLevel, LogLevel.Fatal, fileTarget);
            LogManager.Configuration = configuration;
            LogManager.ReconfigExistingLoggers();
        }
    }


    private static string PrepareLogFilePath(LoggingSettings settings)
    {
        string primaryPath = ResolveLogFilePath(settings.FilePath);
        try
        {
            EnsureLogDirectory(primaryPath);
            return primaryPath;
        }
        catch (UnauthorizedAccessException)
        {
            return PrepareFallbackLogFilePath(settings.FallbackFilePath);
        }
        catch (IOException)
        {
            return PrepareFallbackLogFilePath(settings.FallbackFilePath);
        }
    }

    private static string PrepareFallbackLogFilePath(string configuredPath)
    {
        string fallback = string.IsNullOrWhiteSpace(configuredPath)
            ? AppStoragePaths.LogFile
            : Environment.ExpandEnvironmentVariables(configuredPath.Trim());
        string fallbackPath = Path.GetFullPath(fallback);
        EnsureLogDirectory(fallbackPath);
        return fallbackPath;
    }

    private static void EnsureLogDirectory(string logFilePath)
    {
        string logDirectory = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }
    }

    private static string ResolveLogFilePath(string configuredPath)
    {
        string path = string.IsNullOrWhiteSpace(configuredPath)
            ? AppStoragePaths.LogFile
            : Environment.ExpandEnvironmentVariables(configuredPath.Trim());

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(AppStoragePaths.LogsDirectory, path));
    }

    private static LogLevel ParseLevel(string value, LogLevel fallback)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "TRACE" => LogLevel.Trace,
            "DEBUG" => LogLevel.Debug,
            "INFO" => LogLevel.Info,
            "WARN" or "WARNING" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            "FATAL" => LogLevel.Fatal,
            _ => fallback,
        };
    }
}
