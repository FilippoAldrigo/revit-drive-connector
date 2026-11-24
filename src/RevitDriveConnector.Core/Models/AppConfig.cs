using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Models
{
    public class AppConfig
    {
        private static readonly object CacheLock = new();
        private static AppConfig? _cachedConfig;
        private static DateTime _cachedFileWriteUtc = DateTime.MinValue;
        private static DateTime _lastCheckUtc = DateTime.MinValue;

        [JsonPropertyName("localCachePath")]
        public string LocalCachePath { get; set; } = string.Empty;

        [JsonPropertyName("syncTimeoutSeconds")]
        public int SyncTimeoutSeconds { get; set; } = 120;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "Auto";

        [JsonPropertyName("enableLiveElementLocks")]
        public bool EnableLiveElementLocks { get; set; } = true;

        /// <summary>
        /// Chunk size in MB used for Google Drive resumable uploads (must be a multiple of 256 KB; default 8 MB).
        /// </summary>
        [JsonPropertyName("uploadChunkSizeMb")]
        public int UploadChunkSizeMb { get; set; } = 8;

        public static string GetConfigFilePath()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "config.json");
        }

        public static void InvalidateCache()
        {
            lock (CacheLock)
            {
                _cachedConfig = null;
                _cachedFileWriteUtc = DateTime.MinValue;
                _lastCheckUtc = DateTime.MinValue;
            }
        }

        public static AppConfig Load()
        {
            lock (CacheLock)
            {
                var now = DateTime.UtcNow;
                if (_cachedConfig != null && (now - _lastCheckUtc).TotalMilliseconds < 1000)
                {
                    return _cachedConfig.Clone();
                }

                _lastCheckUtc = now;

                try
                {
                    string path = GetConfigFilePath();
                    if (File.Exists(path))
                    {
                        var writeUtc = File.GetLastWriteTimeUtc(path);
                        if (_cachedConfig != null && writeUtc == _cachedFileWriteUtc)
                        {
                            return _cachedConfig.Clone();
                        }

                        string json = File.ReadAllText(path);
                        var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                        if (cfg != null)
                        {
                            cfg.EnsureDefaults();
                            _cachedConfig = cfg;
                            _cachedFileWriteUtc = writeUtc;
                            return cfg.Clone();
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("AppConfig", "Errore lettura config.json, uso configurazione predefinita", ex);
                }

                var defaultConfig = new AppConfig();
                defaultConfig.EnsureDefaults();
                defaultConfig.Save();
                return defaultConfig.Clone();
            }
        }

        public void Save()
        {
            EnsureDefaults();
            lock (CacheLock)
            {
                try
                {
                    string path = GetConfigFilePath();
                    string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
                    string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, path, true);

                    _cachedConfig = Clone();
                    _cachedFileWriteUtc = File.GetLastWriteTimeUtc(path);
                    _lastCheckUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("AppConfig", "Errore salvataggio config.json", ex);
                }
            }
        }

        public void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(LocalCachePath))
            {
                LocalCachePath = @"C:\RevitDriveCache";
            }

            if (SyncTimeoutSeconds < 60)
            {
                SyncTimeoutSeconds = 120;
            }

            if (UploadChunkSizeMb < 1 || UploadChunkSizeMb > 64)
            {
                UploadChunkSizeMb = 8;
            }

            if (string.IsNullOrWhiteSpace(Language))
            {
                Language = "Auto";
            }

            try
            {
                Directory.CreateDirectory(LocalCachePath);
            }
            catch { }
        }

        public AppConfig Clone() => new()
        {
            LocalCachePath = LocalCachePath,
            SyncTimeoutSeconds = SyncTimeoutSeconds,
            Language = Language,
            EnableLiveElementLocks = EnableLiveElementLocks,
            UploadChunkSizeMb = UploadChunkSizeMb
        };
    }
}
