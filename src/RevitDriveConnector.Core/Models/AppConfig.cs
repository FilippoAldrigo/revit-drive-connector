using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.Models
{
    public class AppConfig
    {
        [JsonPropertyName("coordinatorUrl")]
        public string CoordinatorUrl { get; set; } = "http://localhost:5000";

        [JsonPropertyName("cloudStoragePath")]
        public string CloudStoragePath { get; set; } = string.Empty;

        [JsonPropertyName("localCachePath")]
        public string LocalCachePath { get; set; } = string.Empty;

        [JsonPropertyName("autoStartServices")]
        public bool AutoStartServices { get; set; } = true;

        [JsonPropertyName("syncTimeoutSeconds")]
        public int SyncTimeoutSeconds { get; set; } = 90;

        [JsonPropertyName("language")]
        public string Language { get; set; } = "Auto";

        [JsonPropertyName("enableLiveElementLocks")]
        public bool EnableLiveElementLocks { get; set; } = true;

        public static string GetConfigFilePath()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector");
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "config.json");
        }

        public static AppConfig Load()
        {
            try
            {
                string path = GetConfigFilePath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null)
                    {
                        cfg.EnsureDefaults();
                        return cfg;
                    }
                }
            }
            catch { }

            var defaultConfig = new AppConfig();
            defaultConfig.EnsureDefaults();
            defaultConfig.Save();
            return defaultConfig;
        }

        public void Save()
        {
            try
            {
                string path = GetConfigFilePath();
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }

        public void EnsureDefaults()
        {
            if (string.IsNullOrWhiteSpace(LocalCachePath))
            {
                LocalCachePath = @"C:\RevitDriveCache";
            }

            if (string.IsNullOrWhiteSpace(CloudStoragePath))
            {
                var candidateRoots = new List<string>
                {
                    @"G:\Il mio Drive",
                    @"G:\My Drive",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Il mio Drive"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "My Drive")
                };

                try
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (!drive.IsReady) continue;
                        string dRoot = drive.RootDirectory.FullName;
                        string itPath = Path.Combine(dRoot, "Il mio Drive");
                        string enPath = Path.Combine(dRoot, "My Drive");
                        if (!candidateRoots.Contains(itPath)) candidateRoots.Add(itPath);
                        if (!candidateRoots.Contains(enPath)) candidateRoots.Add(enPath);
                    }
                }
                catch { }

                foreach (var driveFolder in candidateRoots)
                {
                    if (Directory.Exists(driveFolder))
                    {
                        CloudStoragePath = Path.Combine(driveFolder, "RevitProjects");
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(CloudStoragePath))
                {
                    CloudStoragePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RevitDrive_CloudStorage");
                }
            }

            try
            {
                Directory.CreateDirectory(LocalCachePath);
                Directory.CreateDirectory(CloudStoragePath);
            }
            catch { }
        }
    }
}
