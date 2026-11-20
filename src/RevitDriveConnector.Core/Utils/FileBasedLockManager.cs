using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Core.Utils
{
    public class FileLockData
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("lockToken")]
        public string LockToken { get; set; } = string.Empty;

        [JsonPropertyName("acquiredAtUtc")]
        public DateTime AcquiredAtUtc { get; set; }

        [JsonPropertyName("expiresAtUtc")]
        public DateTime ExpiresAtUtc { get; set; }

        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; } = 90;

        [JsonPropertyName("skewToleranceSeconds")]
        public int SkewToleranceSeconds { get; set; } = 15;
    }

    public static class FileBasedLockManager
    {
        private static readonly object _fileLockSync = new();
        public const int DefaultSkewToleranceSeconds = 15;

        public static string GetLockFilePath(string cloudProjectDir, string? lockToken = null)
        {
            string connectorDir = Path.Combine(cloudProjectDir, ".connector");
            if (!string.IsNullOrWhiteSpace(lockToken))
            {
                return Path.Combine(connectorDir, $"sync_lock_{lockToken}.json");
            }
            return Path.Combine(connectorDir, "sync_lock.json");
        }

        private static List<(string path, FileLockData data)> LoadLockFilesInternal(string connectorDir, DateTime now)
        {
            var active = new List<(string path, FileLockData data)>();
            if (!Directory.Exists(connectorDir)) return active;

            var candidates = new List<string>();
            string primary = Path.Combine(connectorDir, "sync_lock.json");
            if (File.Exists(primary)) candidates.Add(primary);
            candidates.AddRange(Directory.GetFiles(connectorDir, "sync_lock_*.json"));

            foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var data = JsonSerializer.Deserialize<FileLockData>(json);
                    if (data == null) continue;

                    int tolerance = data.SkewToleranceSeconds > 0 ? data.SkewToleranceSeconds : DefaultSkewToleranceSeconds;
                    if (data.ExpiresAtUtc.AddSeconds(tolerance) > now)
                    {
                        active.Add((file, data));
                    }
                    else
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                catch { }
            }

            return active;
        }

        public static FileLockData? GetActiveLock(string cloudProjectDir)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    var now = NetworkTimeHelper.GetAccurateUtc();
                    var active = LoadLockFilesInternal(connectorDir, now);
                    if (active.Count == 0) return null;

                    return active
                        .OrderBy(x => Path.GetFileName(x.path).Equals("sync_lock.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(x => x.data.AcquiredAtUtc)
                        .First()
                        .data;
                }
                catch { }

                return null;
            }
        }

        public static void SaveLocalLockSnapshot(string cloudProjectDir, FileLockData lockData)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    Directory.CreateDirectory(connectorDir);
                    var now = NetworkTimeHelper.GetAccurateUtc();
                    LoadLockFilesInternal(connectorDir, now);
                    WriteLockAtomic(GetLockFilePath(cloudProjectDir, lockData.LockToken), lockData);
                }
                catch { }
            }
        }

        public static LockResponse AcquireLock(string cloudProjectDir, string projectId, string user, string machine, int timeoutSeconds = 90)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    Directory.CreateDirectory(connectorDir);
                    string lockFilePath = Path.Combine(connectorDir, "sync_lock.json");

                    var now = NetworkTimeHelper.GetAccurateUtc();
                    var activeLocks = LoadLockFilesInternal(connectorDir, now);

                    var otherLock = activeLocks
                        .Select(x => x.data)
                        .FirstOrDefault(l =>
                            !(l.User.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                              l.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase)) &&
                            !(l.AcquiredAtUtc > now.AddSeconds(60)));

                    if (otherLock != null)
                    {
                        int tolerance = otherLock.SkewToleranceSeconds > 0 ? otherLock.SkewToleranceSeconds : DefaultSkewToleranceSeconds;
                        int remainingSeconds = Math.Max(1, (int)(otherLock.ExpiresAtUtc.AddSeconds(tolerance) - now).TotalSeconds);
                        return new LockResponse
                        {
                            Status = SyncLockStatus.LockDeniedBusy,
                            LockToken = null,
                            CurrentHolder = otherLock.User,
                            CurrentHolderMachine = otherLock.Machine,
                            AcquiredAtUtc = otherLock.AcquiredAtUtc,
                            ExpiresAtUtc = otherLock.ExpiresAtUtc,
                            Message = $"Il progetto è in fase di sincronizzazione su Google Drive da {otherLock.User} ({otherLock.Machine}). Riprova tra circa {remainingSeconds}s."
                        };
                    }

                    var ownLock = activeLocks.FirstOrDefault(x =>
                        x.data.User.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                        x.data.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase));

                    if (ownLock.data != null)
                    {
                        ownLock.data.ExpiresAtUtc = now.AddSeconds(timeoutSeconds);
                        ownLock.data.DurationSeconds = timeoutSeconds;
                        WriteLockAtomic(ownLock.path, ownLock.data);

                        return new LockResponse
                        {
                            Status = SyncLockStatus.LockAcquired,
                            LockToken = ownLock.data.LockToken,
                            CurrentHolder = ownLock.data.User,
                            CurrentHolderMachine = ownLock.data.Machine,
                            AcquiredAtUtc = ownLock.data.AcquiredAtUtc,
                            ExpiresAtUtc = ownLock.data.ExpiresAtUtc,
                            Message = "Lock Google Drive prolungato con successo."
                        };
                    }

                    var newLock = new FileLockData
                    {
                        ProjectId = projectId,
                        User = user,
                        Machine = machine,
                        LockToken = Guid.NewGuid().ToString("N"),
                        AcquiredAtUtc = now,
                        ExpiresAtUtc = now.AddSeconds(timeoutSeconds),
                        DurationSeconds = timeoutSeconds,
                        SkewToleranceSeconds = DefaultSkewToleranceSeconds
                    };

                    WriteLockAtomic(lockFilePath, newLock);

                    return new LockResponse
                    {
                        Status = SyncLockStatus.LockAcquired,
                        LockToken = newLock.LockToken,
                        CurrentHolder = newLock.User,
                        CurrentHolderMachine = newLock.Machine,
                        AcquiredAtUtc = newLock.AcquiredAtUtc,
                        ExpiresAtUtc = newLock.ExpiresAtUtc,
                        Message = "Lock Google Drive acquisito con successo (con sincronizzazione UTC e tolleranza clock-skew)."
                    };
                }
                catch (Exception ex)
                {
                    return new LockResponse
                    {
                        Status = SyncLockStatus.Error,
                        Message = $"Errore scrittura lock su Google Drive: {ex.Message}"
                    };
                }
            }
        }

        public static bool ReleaseLock(string cloudProjectDir, string lockToken, string user)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    if (!Directory.Exists(connectorDir)) return true;

                    var now = NetworkTimeHelper.GetAccurateUtc();
                    var candidates = new List<string>();
                    string primary = Path.Combine(connectorDir, "sync_lock.json");
                    if (File.Exists(primary)) candidates.Add(primary);
                    candidates.AddRange(Directory.GetFiles(connectorDir, "sync_lock_*.json"));

                    bool anyRemainingOwnedByOther = false;
                    foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            var currentLock = JsonSerializer.Deserialize<FileLockData>(json);
                            int tolerance = currentLock?.SkewToleranceSeconds > 0 ? currentLock.SkewToleranceSeconds : DefaultSkewToleranceSeconds;

                            bool matchesToken = !string.IsNullOrWhiteSpace(lockToken) &&
                                                currentLock != null &&
                                                string.Equals(currentLock.LockToken, lockToken, StringComparison.OrdinalIgnoreCase);
                            bool matchesUserWithoutToken = string.IsNullOrWhiteSpace(lockToken) &&
                                                           currentLock != null &&
                                                           currentLock.User.Equals(user, StringComparison.OrdinalIgnoreCase);
                            bool isExpired = currentLock == null || currentLock.ExpiresAtUtc.AddSeconds(tolerance) <= now;

                            if (matchesToken || matchesUserWithoutToken || isExpired)
                            {
                                File.Delete(file);
                            }
                            else
                            {
                                anyRemainingOwnedByOther = true;
                            }
                        }
                        catch
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }

                    return !anyRemainingOwnedByOther;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool HeartbeatLock(string cloudProjectDir, string lockToken, string user, int extendSeconds = 90)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    if (!Directory.Exists(connectorDir)) return false;

                    var candidates = new List<string>();
                    if (!string.IsNullOrWhiteSpace(lockToken))
                    {
                        string tokenFile = GetLockFilePath(cloudProjectDir, lockToken);
                        if (File.Exists(tokenFile)) candidates.Add(tokenFile);
                    }
                    string primary = Path.Combine(connectorDir, "sync_lock.json");
                    if (File.Exists(primary)) candidates.Add(primary);

                    foreach (var lockFilePath in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string json = File.ReadAllText(lockFilePath);
                        var currentLock = JsonSerializer.Deserialize<FileLockData>(json);
                        if (currentLock == null) continue;

                        bool tokenMatches = !string.IsNullOrWhiteSpace(lockToken) &&
                                            string.Equals(currentLock.LockToken, lockToken, StringComparison.OrdinalIgnoreCase);
                        bool userMatchesFallback = string.IsNullOrWhiteSpace(lockToken) &&
                                                   currentLock.User.Equals(user, StringComparison.OrdinalIgnoreCase);

                        if (tokenMatches || userMatchesFallback)
                        {
                            var now = NetworkTimeHelper.GetAccurateUtc();
                            currentLock.ExpiresAtUtc = now.AddSeconds(extendSeconds);
                            WriteLockAtomic(lockFilePath, currentLock);
                            return true;
                        }
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool ForceBreakLock(string cloudProjectDir)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    if (!Directory.Exists(connectorDir)) return true;

                    string primary = Path.Combine(connectorDir, "sync_lock.json");
                    if (File.Exists(primary)) File.Delete(primary);

                    foreach (var file in Directory.GetFiles(connectorDir, "sync_lock_*.json"))
                    {
                        File.Delete(file);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void WriteLockAtomic(string filePath, FileLockData data)
        {
            string tempFile = filePath + ".tmp." + Guid.NewGuid().ToString("N");
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, filePath, true);
        }
    }
}
