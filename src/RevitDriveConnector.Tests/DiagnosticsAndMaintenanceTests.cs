using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services;
using Xunit;

namespace RevitDriveConnector.Tests
{
    public class DiagnosticsAndMaintenanceTests
    {
        [Fact]
        public void DiskSpaceCheck_ReturnsValidDiskInfo()
        {
            var (sufficient, freeBytes, totalBytes, driveLetter) = DiskCleanupService.CheckDiskSpace(Directory.GetCurrentDirectory(), 1024);

            Assert.True(sufficient);
            Assert.True(freeBytes > 0);
            Assert.True(totalBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(driveLetter));
        }

        [Fact]
        public void IntelligentCleanup_RemovesOldTempFilesAndLeavesRecentIntact()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "RdcTest_Cleanup_" + Guid.NewGuid().ToString("N"));
            string cacheDir = Path.Combine(tempRoot, "Cache", "TestProject");
            string snapshotsDir = Path.Combine(cacheDir, ".snapshots");
            string appDataDir = Path.Combine(tempRoot, "AppData");

            try
            {
                Directory.CreateDirectory(snapshotsDir);
                Directory.CreateDirectory(appDataDir);

                // Create old temp file (> 24 hours)
                string oldTmp = Path.Combine(cacheDir, "test_old.tmp");
                File.WriteAllText(oldTmp, "old content");
                File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddDays(-2));

                // Create fresh temp file (< 24 hours)
                string freshTmp = Path.Combine(cacheDir, "test_fresh.tmp");
                File.WriteAllText(freshTmp, "fresh content");

                // Create old snapshot directories (older than 7 days)
                for (int i = 0; i < 4; i++)
                {
                    string snapPath = Path.Combine(snapshotsDir, $"snapshot_{i}");
                    Directory.CreateDirectory(snapPath);
                    File.WriteAllText(Path.Combine(snapPath, "model.rvt"), "model snapshot");
                    Directory.SetCreationTimeUtc(snapPath, DateTime.UtcNow.AddDays(-10 - i));
                }

                var result = DiskCleanupService.PerformIntelligentCleanup(Path.Combine(tempRoot, "Cache"), appDataDir, maxSnapshotAgeDays: 7);

                Assert.True(result.DeletedFilesCount >= 1);
                Assert.False(File.Exists(oldTmp));
                Assert.True(File.Exists(freshTmp));
                // 2 most recent snapshots must be kept even if older than 7 days
                var remainingSnapshots = Directory.GetDirectories(snapshotsDir);
                Assert.True(remainingSnapshots.Length >= 2);
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        [Fact]
        public async Task DiagnosticsZip_GeneratesValidArchiveWithRequiredFiles()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "RdcTest_Diag_" + Guid.NewGuid().ToString("N"));
            string zipPath = Path.Combine(tempRoot, "diagnostics_test.zip");

            try
            {
                Directory.CreateDirectory(tempRoot);

                var cfg = new AppConfig
                {
                    LocalCachePath = Path.Combine(tempRoot, "Cache"),
                    Language = "it"
                };
                Directory.CreateDirectory(cfg.LocalCachePath);

                var driveService = new GoogleDriveService(cfg, customAppDataDir: Path.Combine(tempRoot, "AppData"));

                var res = await DiagnosticsService.ExportDiagnosticsZipAsync(zipPath, cfg, driveService);

                Assert.True(File.Exists(zipPath));
                Assert.True(res.FileCount >= 2);
                Assert.True(res.ZipSizeBytes > 0);

                using var archive = ZipFile.OpenRead(zipPath);
                Assert.NotNull(archive.GetEntry("diagnostics_summary.txt"));
                Assert.NotNull(archive.GetEntry("config_sanitized.json"));
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        [Fact]
        public async Task PreflightCheck_HandlesLowDiskGracefully()
        {
            var req = new IpcRequest
            {
                Command = IpcCommand.PreflightCheck,
                PayloadJson = JsonSerializer.Serialize(new PreflightCheckRequest
                {
                    ProjectId = "TestProject",
                    MinRequiredFreeBytes = long.MaxValue // Impossibly high space requirement
                })
            };

            var resp = await LocalServiceEngine.HandleIpcRequestAsync(req);

            Assert.NotNull(resp);
            var result = JsonSerializer.Deserialize<PreflightCheckResult>(resp.DataJson ?? "");
            Assert.NotNull(result);
            Assert.False(result.IsDiskSpaceSufficient);
            Assert.False(result.Success);
        }
    }
}
