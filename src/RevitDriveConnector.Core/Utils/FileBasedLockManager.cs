using System;
using System.IO;
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

        public static FileLockData? GetActiveLock(string cloudProjectDir)
        {
            lock (_fileLockSync)
            {
                try
                {
                    string lockFilePath = Path.Combine(cloudProjectDir, ".connector", "sync_lock.json");
                    if (!File.Exists(lockFilePath)) return null;

                    string json = File.ReadAllText(lockFilePath);
                    var currentLock = JsonSerializer.Deserialize<FileLockData>(json);
                    if (currentLock == null) return null;

                    var now = NetworkTimeHelper.GetAccurateUtc();
                    int tolerance = currentLock.SkewToleranceSeconds > 0 ? currentLock.SkewToleranceSeconds : DefaultSkewToleranceSeconds;
                    if (currentLock.ExpiresAtUtc.AddSeconds(tolerance) > now)
                    {
                        return currentLock;
                    }
                }
                catch { }

                return null;
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

                    if (File.Exists(lockFilePath))
                    {
                        try
                        {
                            string json = File.ReadAllText(lockFilePath);
                            var currentLock = JsonSerializer.Deserialize<FileLockData>(json);

                            if (currentLock != null)
                            {
                                int tolerance = currentLock.SkewToleranceSeconds > 0 ? currentLock.SkewToleranceSeconds : DefaultSkewToleranceSeconds;
                                DateTime expirationWithTolerance = currentLock.ExpiresAtUtc.AddSeconds(tolerance);

                                if (expirationWithTolerance > now)
                                {
                                    // Stesso utente e stessa macchina: prolunga il lock esistente
                                    if (currentLock.User.Equals(user, StringComparison.OrdinalIgnoreCase) &&
                                        currentLock.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase))
                                    {
                                        currentLock.ExpiresAtUtc = now.AddSeconds(timeoutSeconds);
                                        currentLock.DurationSeconds = timeoutSeconds;
                                        WriteLockAtomic(lockFilePath, currentLock);

                                        return new LockResponse
                                        {
                                            Status = SyncLockStatus.LockAcquired,
                                            LockToken = currentLock.LockToken,
                                            CurrentHolder = currentLock.User,
                                            CurrentHolderMachine = currentLock.Machine,
                                            AcquiredAtUtc = currentLock.AcquiredAtUtc,
                                            ExpiresAtUtc = currentLock.ExpiresAtUtc,
                                            Message = "Lock Google Drive prolungato con successo."
                                        };
                                    }

                                    // Protezione contro orologi eccessivamente avanti nel futuro (> 60s)
                                    if (currentLock.AcquiredAtUtc > now.AddSeconds(60))
                                    {
                                        DateTime fileWriteUtc = File.GetLastWriteTimeUtc(lockFilePath);
                                        if (now > fileWriteUtc.AddSeconds(timeoutSeconds + tolerance))
                                        {
                                            goto CreateNew;
                                        }
                                    }

                                    int remainingSeconds = Math.Max(1, (int)(expirationWithTolerance - now).TotalSeconds);
                                    return new LockResponse
                                    {
                                        Status = SyncLockStatus.LockDeniedBusy,
                                        LockToken = null,
                                        CurrentHolder = currentLock.User,
                                        CurrentHolderMachine = currentLock.Machine,
                                        AcquiredAtUtc = currentLock.AcquiredAtUtc,
                                        ExpiresAtUtc = currentLock.ExpiresAtUtc,
                                        Message = $"Il progetto è in fase di sincronizzazione su Google Drive da {currentLock.User} ({currentLock.Machine}). Riprova tra circa {remainingSeconds}s."
                                    };
                                }
                            }
                        }
                        catch
                        {
                        }
                    }

                CreateNew:
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
                    string lockFilePath = Path.Combine(cloudProjectDir, ".connector", "sync_lock.json");
                    if (!File.Exists(lockFilePath)) return true;

                    try
                    {
                        string json = File.ReadAllText(lockFilePath);
                        var currentLock = JsonSerializer.Deserialize<FileLockData>(json);
                        var now = NetworkTimeHelper.GetAccurateUtc();

                        if (currentLock == null ||
                            currentLock.LockToken == lockToken ||
                            currentLock.User.Equals(user, StringComparison.OrdinalIgnoreCase) ||
                            currentLock.ExpiresAtUtc.AddSeconds(DefaultSkewToleranceSeconds) <= now)
                        {
                            File.Delete(lockFilePath);
                            return true;
                        }
                    }
                    catch
                    {
                        File.Delete(lockFilePath);
                        return true;
                    }

                    return false;
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
                    string lockFilePath = Path.Combine(cloudProjectDir, ".connector", "sync_lock.json");
                    if (!File.Exists(lockFilePath)) return false;

                    string json = File.ReadAllText(lockFilePath);
                    var currentLock = JsonSerializer.Deserialize<FileLockData>(json);

                    if (currentLock != null &&
                        (currentLock.LockToken == lockToken || currentLock.User.Equals(user, StringComparison.OrdinalIgnoreCase)))
                    {
                        var now = NetworkTimeHelper.GetAccurateUtc();
                        currentLock.ExpiresAtUtc = now.AddSeconds(extendSeconds);
                        WriteLockAtomic(lockFilePath, currentLock);
                        return true;
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
                    string lockFilePath = Path.Combine(cloudProjectDir, ".connector", "sync_lock.json");
                    if (File.Exists(lockFilePath))
                    {
                        File.Delete(lockFilePath);
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
