using System;
using System.IO;
using System.Linq;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services
{
    public static class DiskCleanupService
    {
        public static (bool sufficient, long availableBytes, long totalBytes, string driveLetter) CheckDiskSpace(
            string targetPath,
            long requiredBytes = 2L * 1024 * 1024 * 1024)
        {
            try
            {
                string fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(targetPath) ? "C:\\" : targetPath);
                string root = Path.GetPathRoot(fullPath) ?? "C:\\";
                var drive = new DriveInfo(root);

                long freeBytes = drive.AvailableFreeSpace;
                long totalBytes = drive.TotalSize;
                bool ok = freeBytes >= requiredBytes;

                return (ok, freeBytes, totalBytes, drive.Name);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DiskCleanupService", $"Errore controllo spazio disco su '{targetPath}'", ex);
                // In case of error (e.g. UNC path), assume sufficient to not block arbitrarily
                return (true, 50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024, "C:\\");
            }
        }

        public static DiskCleanupResult PerformIntelligentCleanup(
            string localCachePath,
            string appDataDir,
            int maxSnapshotAgeDays = 7)
        {
            var result = new DiskCleanupResult();
            long freed = 0;
            int filesDeleted = 0;
            int dirsDeleted = 0;

            try
            {
                // 1. Cleanup old snapshots in localCachePath
                if (Directory.Exists(localCachePath))
                {
                    var projectDirs = Directory.GetDirectories(localCachePath);
                    foreach (var pDir in projectDirs)
                    {
                        string snapshotsDir = Path.Combine(pDir, ".snapshots");
                        if (!Directory.Exists(snapshotsDir)) continue;

                        try
                        {
                            var snapshotDirs = Directory.GetDirectories(snapshotsDir)
                                .Select(d => new DirectoryInfo(d))
                                .OrderByDescending(di => di.CreationTimeUtc)
                                .ToList();

                            // Always keep the 2 most recent snapshots, delete ones older than maxSnapshotAgeDays
                            var cutoff = DateTime.UtcNow.AddDays(-maxSnapshotAgeDays);
                            for (int i = 2; i < snapshotDirs.Count; i++)
                            {
                                var sDir = snapshotDirs[i];
                                if (sDir.CreationTimeUtc < cutoff)
                                {
                                    long dirSize = GetDirectorySize(sDir.FullName);
                                    sDir.Delete(true);
                                    freed += dirSize;
                                    dirsDeleted++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warn("DiskCleanupService", $"Avviso pulizia snapshot per '{pDir}'", ex);
                        }

                        // Clean old temporary files in project directory (> 24 hours)
                        try
                        {
                            var tempFiles = Directory.GetFiles(pDir, "*.tmp", SearchOption.TopDirectoryOnly)
                                .Concat(Directory.GetFiles(pDir, "*.hashcache.json.tmp", SearchOption.TopDirectoryOnly));

                            var oneDayAgo = DateTime.UtcNow.AddHours(-24);
                            foreach (var tf in tempFiles)
                            {
                                var fi = new FileInfo(tf);
                                if (fi.LastWriteTimeUtc < oneDayAgo)
                                {
                                    freed += fi.Length;
                                    fi.Delete();
                                    filesDeleted++;
                                }
                            }
                        }
                        catch { }
                    }
                }

                // 2. Cleanup AppData temp directory
                string appDataTemp = Path.Combine(appDataDir, "temp");
                if (Directory.Exists(appDataTemp))
                {
                    try
                    {
                        var oldTempFiles = Directory.GetFiles(appDataTemp, "*.*", SearchOption.AllDirectories);
                        var oneDayAgo = DateTime.UtcNow.AddHours(-24);
                        foreach (var f in oldTempFiles)
                        {
                            try
                            {
                                var fi = new FileInfo(f);
                                if (fi.LastWriteTimeUtc < oneDayAgo)
                                {
                                    freed += fi.Length;
                                    fi.Delete();
                                    filesDeleted++;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                result.FreedBytes = freed;
                result.DeletedFilesCount = filesDeleted;
                result.DeletedDirectoriesCount = dirsDeleted;
                double freedMb = freed / (1024.0 * 1024.0);
                result.Message = $"Pulizia completata con successo: liberati {freedMb:F1} MB ({filesDeleted} file temporanei e {dirsDeleted} vecchi snapshot rimossi).";
                AppLogger.Info("DiskCleanupService", result.Message);
            }
            catch (Exception ex)
            {
                AppLogger.Error("DiskCleanupService", "Errore durante esecuzione pulizia disco", ex);
                result.Message = $"Pulizia parziale con avvisi: {ex.Message}";
            }

            return result;
        }

        private static long GetDirectorySize(string path)
        {
            try
            {
                return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                    .Select(f => { try { return new FileInfo(f).Length; } catch { return 0L; } })
                    .Sum();
            }
            catch
            {
                return 0;
            }
        }
    }
}
