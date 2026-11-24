using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services
{
    public static class DiagnosticsService
    {
        public static async Task<DiagnosticsExportResponse> ExportDiagnosticsZipAsync(
            string destinationZipPath,
            AppConfig config,
            GoogleDriveService driveService,
            CancellationToken ct = default)
        {
            string zipDir = Path.GetDirectoryName(destinationZipPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            Directory.CreateDirectory(zipDir);

            if (File.Exists(destinationZipPath))
            {
                File.Delete(destinationZipPath);
            }

            int fileCount = 0;

            using (var zipStream = new FileStream(destinationZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                // 1. Environment and diagnostics summary
                var summarySb = new StringBuilder();
                summarySb.AppendLine("=== REVIT DRIVE CONNECTOR DIAGNOSTICS REPORT ===");
                summarySb.AppendLine($"Generated At (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
                summarySb.AppendLine($"Machine: {Environment.MachineName}");
                summarySb.AppendLine($"User Domain/Name: {Environment.UserDomainName}\\{Environment.UserName}");
                summarySb.AppendLine($"OS Version: {Environment.OSVersion} (64-bit: {Environment.Is64BitOperatingSystem})");
                summarySb.AppendLine($".NET Runtime: {Environment.Version}");
                summarySb.AppendLine($"App Version: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
                summarySb.AppendLine();

                summarySb.AppendLine("--- GOOGLE DRIVE AUTHENTICATION ---");
                summarySb.AppendLine($"Is Connected: {driveService.IsNativeGoogleDriveConnected}");
                summarySb.AppendLine($"Connected Email: {driveService.ConnectedEmail}");
                summarySb.AppendLine($"Credentials File Present: {File.Exists(driveService.CredentialsPath)}");
                summarySb.AppendLine($"Token Store Exists: {Directory.Exists(driveService.TokenStorePath)}");
                summarySb.AppendLine();

                summarySb.AppendLine("--- TIME SYNCHRONIZATION ---");
                summarySb.AppendLine($"Is Synchronized: {NetworkTimeHelper.IsTimeSynchronized}");
                summarySb.AppendLine($"Clock Offset (ms): {NetworkTimeHelper.CurrentClockOffset.TotalMilliseconds:F1}");
                summarySb.AppendLine();

                summarySb.AppendLine("--- STORAGE & DISK CACHE ---");
                summarySb.AppendLine($"Local Cache Path: {config.LocalCachePath}");
                var (sufficient, freeBytes, totalBytes, driveLetter) = DiskCleanupService.CheckDiskSpace(config.LocalCachePath, 1);
                summarySb.AppendLine($"Drive: {driveLetter}");
                summarySb.AppendLine($"Available Free: {freeBytes / (1024.0 * 1024.0 * 1024.0):F2} GB");
                summarySb.AppendLine($"Total Capacity: {totalBytes / (1024.0 * 1024.0 * 1024.0):F2} GB");
                summarySb.AppendLine();

                summarySb.AppendLine("--- AUTODESK REVIT DETECTED ---");
                foreach (int year in new[] { 2025, 2026, 2027 })
                {
                    string addinPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Autodesk", "Revit", "Addins", year.ToString(), "RevitDriveConnector.addin");
                    summarySb.AppendLine($"Revit {year} Addin Manifest: {(File.Exists(addinPath) ? "INSTALLED (" + addinPath + ")" : "Not found")}");
                }

                var summaryEntry = archive.CreateEntry("diagnostics_summary.txt", CompressionLevel.Optimal);
                using (var entryStream = summaryEntry.Open())
                using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                {
                    await writer.WriteAsync(summarySb.ToString());
                }
                fileCount++;

                // 2. Sanitized Config
                var sanitizedConfig = new
                {
                    LocalCachePath = config.LocalCachePath,
                    SyncTimeoutSeconds = config.SyncTimeoutSeconds,
                    UploadChunkSizeMb = config.UploadChunkSizeMb,
                    EnableLiveElementLocks = config.EnableLiveElementLocks,
                    Language = config.Language,
                    GoogleClientCredentialsConfigured = File.Exists(driveService.CredentialsPath)
                };

                var configEntry = archive.CreateEntry("config_sanitized.json", CompressionLevel.Optimal);
                using (var entryStream = configEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(entryStream, sanitizedConfig, new JsonSerializerOptions { WriteIndented = true }, ct);
                }
                fileCount++;

                // 3. Log files from AppLogger.LogDirectory (last 7 days, max 10 files)
                if (Directory.Exists(AppLogger.LogDirectory))
                {
                    try
                    {
                        var logFiles = Directory.GetFiles(AppLogger.LogDirectory, "*.log")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(fi => fi.LastWriteTimeUtc)
                            .Take(10)
                            .ToList();

                        foreach (var logFile in logFiles)
                        {
                            try
                            {
                                var logEntry = archive.CreateEntry($"logs/{logFile.Name}", CompressionLevel.Optimal);
                                using var fs = new FileStream(logFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                using var entryStream = logEntry.Open();
                                await fs.CopyToAsync(entryStream, ct);
                                fileCount++;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // 4. Manifest files from cached projects
                if (Directory.Exists(config.LocalCachePath))
                {
                    try
                    {
                        var pDirs = Directory.GetDirectories(config.LocalCachePath);
                        foreach (var pDir in pDirs)
                        {
                            string pName = Path.GetFileName(pDir);
                            string manifestFile = Path.Combine(pDir, "manifest.json");
                            if (File.Exists(manifestFile))
                            {
                                try
                                {
                                    var manifestEntry = archive.CreateEntry($"manifests/{pName}_manifest.json", CompressionLevel.Optimal);
                                    using var fs = new FileStream(manifestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    using var entryStream = manifestEntry.Open();
                                    await fs.CopyToAsync(entryStream, ct);
                                    fileCount++;
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }

            var zipInfo = new FileInfo(destinationZipPath);
            return new DiagnosticsExportResponse
            {
                ZipPath = destinationZipPath,
                ZipSizeBytes = zipInfo.Exists ? zipInfo.Length : 0,
                FileCount = fileCount
            };
        }
    }
}
