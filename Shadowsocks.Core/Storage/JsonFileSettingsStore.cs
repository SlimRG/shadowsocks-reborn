using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shadowsocks.Core.Storage
{
    public sealed class JsonFileSettingsStore : ISettingsStore
    {
        private readonly object syncRoot = new();
        private readonly string filePath;
        private readonly string backupPath;

        public JsonFileSettingsStore(string filePath, string backupPath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Settings file path must not be empty.", nameof(filePath));
            if (string.IsNullOrWhiteSpace(backupPath))
                throw new ArgumentException("Settings backup path must not be empty.", nameof(backupPath));

            this.filePath = Path.GetFullPath(filePath);
            this.backupPath = Path.GetFullPath(backupPath);
        }

        public bool TryGetString(string name, out string value)
        {
            lock (syncRoot)
            {
                JObject document = LoadDocument();
                if (!document.TryGetValue(name, StringComparison.Ordinal, out JToken token)
                    || token is null
                    || token.Type == JTokenType.Null)
                {
                    value = null;
                    return false;
                }

                value = token.Type == JTokenType.String
                    ? token.Value<string>()
                    : token.ToString(Formatting.Indented);
                return value is not null;
            }
        }

        public void SetString(string name, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);

            lock (syncRoot)
            {
                JObject document = LoadDocument();
                document[name] = ParseJsonValue(value);
                SaveDocument(document);
            }
        }

        public bool TryGetInt32(string name, out int value)
        {
            lock (syncRoot)
            {
                JObject document = LoadDocument();
                if (document.TryGetValue(name, StringComparison.Ordinal, out JToken token)
                    && token is not null
                    && token.Type == JTokenType.Integer)
                {
                    value = token.Value<int>();
                    return true;
                }

                value = default;
                return false;
            }
        }

        public void SetInt32(string name, int value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            lock (syncRoot)
            {
                JObject document = LoadDocument();
                document[name] = value;
                SaveDocument(document);
            }
        }

        public void DeleteValue(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            lock (syncRoot)
            {
                JObject document = LoadDocument();
                if (document.Remove(name))
                {
                    SaveDocument(document);
                }
            }
        }

        private JObject LoadDocument()
        {
            if (TryLoadDocument(filePath, out JObject document))
                return document;

            if (TryLoadDocument(backupPath, out document))
            {
                TryRestoreBackup(document);
                return document;
            }

            return new JObject();
        }

        private void SaveDocument(JObject document)
        {
            string directory = Path.GetDirectoryName(filePath)
                ?? throw new InvalidOperationException("Settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            string temporaryPath = Path.Combine(
                directory,
                $"{Path.GetFileName(filePath)}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");

            try
            {
                File.WriteAllText(temporaryPath, document.ToString(Formatting.Indented));

                if (File.Exists(filePath))
                {
                    File.Copy(filePath, backupPath, overwrite: true);
                    File.Replace(temporaryPath, filePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, filePath);
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private void TryRestoreBackup(JObject document)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath)
                    ?? throw new InvalidOperationException("Settings path has no parent directory.");
                Directory.CreateDirectory(directory);
                string temporaryPath = Path.Combine(
                    directory,
                    $"{Path.GetFileName(filePath)}.restore.{Environment.ProcessId}.{Guid.NewGuid():N}");
                try
                {
                    File.WriteAllText(temporaryPath, document.ToString(Formatting.Indented));
                    File.Move(temporaryPath, filePath, overwrite: true);
                }
                finally
                {
                    TryDelete(temporaryPath);
                }
            }
            catch
            {
                // The caller can still use the valid backup in memory even if the primary
                // file cannot be repaired at this moment.
            }
        }

        private static bool TryLoadDocument(string path, out JObject document)
        {
            document = null;
            if (!File.Exists(path))
                return false;

            try
            {
                string json = File.ReadAllText(path);
                document = JObject.Parse(json);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static JToken ParseJsonValue(string value)
        {
            try
            {
                return JToken.Parse(value);
            }
            catch (JsonException)
            {
                return new JValue(value);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
