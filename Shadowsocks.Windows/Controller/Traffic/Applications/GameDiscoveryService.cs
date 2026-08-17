using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using NLog;

namespace Shadowsocks.Controller.Traffic.Applications
{
    public sealed class DiscoveredGame
    {
        public DiscoveredGame(string name, string executablePath, string source)
        {
            Name = name ?? string.Empty;
            ExecutablePath = executablePath ?? string.Empty;
            Source = source ?? string.Empty;
        }

        public string Name { get; }
        public string ExecutablePath { get; }
        public string Source { get; }
    }

    /// <summary>
    /// Best-effort local game discovery for Game Mode suggestions. It only reads
    /// launcher manifests/registry entries and common game-library directories;
    /// it never changes launcher configuration and never replaces manual rules.
    /// </summary>
    public static class GameDiscoveryService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly Regex VdfPathRegex = new("\\\"path\\\"\\s+\\\"(?<value>[^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AcfValueRegex = new("\\\"(?<key>[^\\\"]+)\\\"\\s+\\\"(?<value>[^\\\"]*)\\\"", RegexOptions.Compiled);
        private static readonly string[] RejectedExecutableTokens =
        [
            "unins", "uninstall", "setup", "install", "crash", "report", "launcher", "updater", "update",
            "helper", "bootstrap", "redist", "vcredist", "vc_redist", "dxsetup", "easyanticheat", "battleye",
            "unitycrashhandler", "cefprocess", "notification", "overlay", "benchmark", "config"
        ];
        private static readonly string[] RejectedDirectoryTokens =
        [
            "_commonredist", "redist", "redistributable", "installer", "support", "prereq", "crashreporter",
            "easyanticheat", "battleye", "dotnet", "directx"
        ];

        public static IReadOnlyList<DiscoveredGame> Discover()
        {
            var results = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);
            TryDiscoverSteam(results);
            TryDiscoverEpic(results);
            TryDiscoverGog(results);
            TryDiscoverXbox(results);

            return results.Values
                .Where(game => File.Exists(game.ExecutablePath))
                .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(game => game.Source, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void TryDiscoverSteam(Dictionary<string, DiscoveredGame> results)
        {
            try
            {
                var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddRegistryPath(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                AddRegistryPath(steamRoots, Registry.LocalMachine, @"Software\WOW6432Node\Valve\Steam", "InstallPath");
                AddRegistryPath(steamRoots, Registry.LocalMachine, @"Software\Valve\Steam", "InstallPath");
                string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(programFilesX86))
                {
                    steamRoots.Add(Path.Combine(programFilesX86, "Steam"));
                }

                var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in steamRoots.Where(Directory.Exists))
                {
                    libraries.Add(root);
                    string libraryFile = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                    if (!File.Exists(libraryFile))
                    {
                        continue;
                    }

                    string text = File.ReadAllText(libraryFile);
                    foreach (Match match in VdfPathRegex.Matches(text))
                    {
                        string path = match.Groups["value"].Value.Replace("\\\\", "\\");
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            libraries.Add(path);
                        }
                    }
                }

                foreach (string library in libraries.Where(Directory.Exists))
                {
                    string steamApps = Path.Combine(library, "steamapps");
                    if (!Directory.Exists(steamApps))
                    {
                        continue;
                    }

                    foreach (string manifest in SafeEnumerateFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            Dictionary<string, string> values = ParseAcfValues(File.ReadAllText(manifest));
                            if (!values.TryGetValue("installdir", out string installDir) || string.IsNullOrWhiteSpace(installDir))
                            {
                                continue;
                            }
                            string name = values.TryGetValue("name", out string manifestName) && !string.IsNullOrWhiteSpace(manifestName)
                                ? manifestName
                                : installDir;
                            string gameDirectory = Path.Combine(steamApps, "common", installDir);
                            string executable = FindBestExecutable(gameDirectory, name, installDir);
                            AddResult(results, name, executable, "Steam");
                        }
                        catch (Exception exception)
                        {
                            Logger.Debug(exception, "Unable to inspect Steam manifest {0}", manifest);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Steam game discovery failed.");
            }
        }

        private static void TryDiscoverEpic(Dictionary<string, DiscoveredGame> results)
        {
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string manifests = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
                foreach (string manifest in SafeEnumerateFiles(manifests, "*.item", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        JObject json = JObject.Parse(File.ReadAllText(manifest));
                        string name = json.Value<string>("DisplayName") ?? json.Value<string>("AppName") ?? Path.GetFileNameWithoutExtension(manifest);
                        string installLocation = json.Value<string>("InstallLocation") ?? string.Empty;
                        string launchExecutable = json.Value<string>("LaunchExecutable") ?? string.Empty;
                        string executable = ResolveExecutable(installLocation, launchExecutable);
                        if (string.IsNullOrWhiteSpace(executable))
                        {
                            executable = FindBestExecutable(installLocation, name, Path.GetFileName(installLocation));
                        }
                        AddResult(results, name, executable, "Epic Games");
                    }
                    catch (Exception exception)
                    {
                        Logger.Debug(exception, "Unable to inspect Epic manifest {0}", manifest);
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Epic game discovery failed.");
            }
        }

        private static void TryDiscoverGog(Dictionary<string, DiscoveredGame> results)
        {
            TryDiscoverGogRegistry(results, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\Games");
            TryDiscoverGogRegistry(results, Registry.LocalMachine, @"SOFTWARE\GOG.com\Games");
            TryDiscoverGogRegistry(results, Registry.CurrentUser, @"SOFTWARE\GOG.com\Games");
        }

        private static void TryDiscoverGogRegistry(Dictionary<string, DiscoveredGame> results, RegistryKey root, string subKeyPath)
        {
            try
            {
                using RegistryKey games = root.OpenSubKey(subKeyPath, writable: false);
                if (games is null)
                {
                    return;
                }
                foreach (string subKeyName in games.GetSubKeyNames())
                {
                    using RegistryKey game = games.OpenSubKey(subKeyName, writable: false);
                    if (game is null)
                    {
                        continue;
                    }
                    string name = game.GetValue("gameName")?.ToString() ?? game.GetValue("gameNameBase")?.ToString() ?? subKeyName;
                    string installDirectory = game.GetValue("path")?.ToString() ?? string.Empty;
                    string executableValue = game.GetValue("exe")?.ToString() ?? string.Empty;
                    string executable = ResolveExecutable(installDirectory, executableValue);
                    if (string.IsNullOrWhiteSpace(executable))
                    {
                        executable = FindBestExecutable(installDirectory, name, Path.GetFileName(installDirectory));
                    }
                    AddResult(results, name, executable, "GOG");
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "GOG game discovery failed for {0}", subKeyPath);
            }
        }

        private static void TryDiscoverXbox(Dictionary<string, DiscoveredGame> results)
        {
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network)
                    {
                        continue;
                    }
                    string xboxRoot = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                    foreach (string gameDirectory in SafeEnumerateDirectories(xboxRoot))
                    {
                        string content = Path.Combine(gameDirectory, "Content");
                        string searchRoot = Directory.Exists(content) ? content : gameDirectory;
                        string name = Path.GetFileName(gameDirectory);
                        string executable = FindBestExecutable(searchRoot, name, name);
                        AddResult(results, name, executable, "Xbox");
                    }
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Xbox game discovery failed.");
            }
        }

        private static void AddRegistryPath(HashSet<string> paths, RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using RegistryKey key = root.OpenSubKey(subKey, writable: false);
                string value = key?.GetValue(valueName)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    paths.Add(Environment.ExpandEnvironmentVariables(value));
                }
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "Unable to read game-library registry path {0}\\{1}", subKey, valueName);
            }
        }

        private static Dictionary<string, string> ParseAcfValues(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in AcfValueRegex.Matches(text ?? string.Empty))
            {
                string key = match.Groups["key"].Value;
                string value = match.Groups["value"].Value.Replace("\\\\", "\\");
                if (!values.ContainsKey(key))
                {
                    values[key] = value;
                }
            }
            return values;
        }

        private static string ResolveExecutable(string installDirectory, string executable)
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return string.Empty;
            }
            string expanded = Environment.ExpandEnvironmentVariables(executable.Trim().Trim('"'));
            if (Path.IsPathRooted(expanded))
            {
                return File.Exists(expanded) ? Path.GetFullPath(expanded) : string.Empty;
            }
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return string.Empty;
            }
            string combined = Path.GetFullPath(Path.Combine(installDirectory, expanded.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(combined) ? combined : string.Empty;
        }

        private static string FindBestExecutable(string directory, string displayName, string installName)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return string.Empty;
            }

            string normalizedName = NormalizeName(displayName);
            string normalizedInstall = NormalizeName(installName);
            string bestPath = string.Empty;
            int bestScore = int.MinValue;

            foreach ((string path, int depth) in EnumerateExecutables(directory, maxDepth: 4, maxFiles: 300))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                string normalizedFile = NormalizeName(fileName);
                string lower = fileName.ToLowerInvariant();
                if (RejectedExecutableTokens.Any(token => lower.Contains(token, StringComparison.Ordinal)))
                {
                    continue;
                }

                int score = 0;
                if (!string.IsNullOrWhiteSpace(normalizedName) && normalizedFile == normalizedName) score += 120;
                if (!string.IsNullOrWhiteSpace(normalizedInstall) && normalizedFile == normalizedInstall) score += 110;
                if (!string.IsNullOrWhiteSpace(normalizedName) && normalizedFile.Contains(normalizedName, StringComparison.Ordinal)) score += 45;
                if (!string.IsNullOrWhiteSpace(normalizedInstall) && normalizedFile.Contains(normalizedInstall, StringComparison.Ordinal)) score += 40;
                if (lower.Contains("shipping", StringComparison.Ordinal)) score += 25;
                if (path.Contains("Binaries", StringComparison.OrdinalIgnoreCase)) score += 12;
                if (path.Contains("Win64", StringComparison.OrdinalIgnoreCase) || path.Contains("x64", StringComparison.OrdinalIgnoreCase)) score += 10;
                score += Math.Max(0, 15 - depth * 3);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPath = path;
                }
            }
            return bestPath;
        }

        private static IEnumerable<(string Path, int Depth)> EnumerateExecutables(string root, int maxDepth, int maxFiles)
        {
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((root, 0));
            int yielded = 0;
            while (queue.Count > 0 && yielded < maxFiles)
            {
                (string current, int depth) = queue.Dequeue();
                foreach (string file in SafeEnumerateFiles(current, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    yield return (file, depth);
                    yielded++;
                    if (yielded >= maxFiles) yield break;
                }
                if (depth >= maxDepth)
                {
                    continue;
                }
                foreach (string child in SafeEnumerateDirectories(current))
                {
                    string name = Path.GetFileName(child).ToLowerInvariant();
                    if (RejectedDirectoryTokens.Any(token => name.Contains(token, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        private static IEnumerable<string> SafeEnumerateFiles(string directory, string pattern, SearchOption option)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }
            try
            {
                return Directory.EnumerateFiles(directory, pattern, option).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }
            try
            {
                return Directory.EnumerateDirectories(directory).ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static void AddResult(Dictionary<string, DiscoveredGame> results, string name, string executable, string source)
        {
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                return;
            }
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(executable);
            }
            catch
            {
                return;
            }
            if (!results.ContainsKey(fullPath))
            {
                results[fullPath] = new DiscoveredGame(
                    string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullPath) : name.Trim(),
                    fullPath,
                    source);
            }
        }
    }
}
