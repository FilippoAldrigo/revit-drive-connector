using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services;
using RevitDriveConnector.Core.Utils;
using Xunit;

namespace RevitDriveConnector.Tests
{
    public class CloudSyncAndLockTests
    {
        private static (GoogleDriveService Service, string CacheRoot) CreateTestService(FakeDriveHttpHandler fakeDrive)
        {
            string cacheRoot = Path.Combine(Path.GetTempPath(), "RdcTestCache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cacheRoot);
            var cfg = new AppConfig
            {
                LocalCachePath = cacheRoot,
                SyncTimeoutSeconds = 120,
                EnableLiveElementLocks = true,
                UploadChunkSizeMb = 1
            };
            var svc = new GoogleDriveService(cfg, fakeDrive, _ => Task.FromResult("mock-token"), customAppDataDir: cacheRoot);
            return (svc, cacheRoot);
        }

        [Fact]
        public async Task DistributedLock_ArbitratesConcurrentUsersAndSupportsDirectHeartbeatAndRelease()
        {
            var fakeDrive = new FakeDriveHttpHandler();
            var (svcA, cacheA) = CreateTestService(fakeDrive);
            var (svcB, cacheB) = CreateTestService(fakeDrive);

            try
            {
                var rootFolder = fakeDrive.AddFolder("RevitProjects");
                fakeDrive.AddFolder("HospitalBIM", rootFolder.Id);

                var taskA = svcA.AcquireProjectLockAsync(new LockRequest { ProjectId = "HospitalBIM", User = "Filippo", Machine = "PC-A", TimeoutSeconds = 120 });
                var taskB = svcB.AcquireProjectLockAsync(new LockRequest { ProjectId = "HospitalBIM", User = "Marco", Machine = "PC-B", TimeoutSeconds = 120 });

                var results = await Task.WhenAll(taskA, taskB);

                // Exactly ONE teammate must win the lock, and the other MUST receive LockDeniedBusy
                Assert.Single(results, r => r.Status == SyncLockStatus.LockAcquired);
                Assert.Single(results, r => r.Status == SyncLockStatus.LockDeniedBusy);

                var winnerResp = results.Single(r => r.Status == SyncLockStatus.LockAcquired);
                var winnerSvc = winnerResp.CurrentHolder == "Filippo" ? svcA : svcB;

                // Heartbeat O(1) succeeds
                bool hbOk = await winnerSvc.HeartbeatProjectLockAsync("HospitalBIM", winnerResp.LockToken!, winnerResp.CurrentHolder!, 120);
                Assert.True(hbOk);

                // Ownership valid
                var (isValid, _) = await winnerSvc.VerifyLockOwnershipAsync("HospitalBIM", winnerResp.LockToken!, winnerResp.CurrentHolder!);
                Assert.True(isValid);

                // Release lock -> second user can now acquire
                bool released = await winnerSvc.ReleaseProjectLockAsync("HospitalBIM", winnerResp.LockToken!, winnerResp.CurrentHolder!);
                Assert.True(released);

                var secondAcquire = await svcB.AcquireProjectLockAsync(new LockRequest { ProjectId = "HospitalBIM", User = "Marco", Machine = "PC-B", TimeoutSeconds = 120 });
                Assert.Equal(SyncLockStatus.LockAcquired, secondAcquire.Status);
            }
            finally
            {
                if (Directory.Exists(cacheA)) Directory.Delete(cacheA, true);
                if (Directory.Exists(cacheB)) Directory.Delete(cacheB, true);
            }
        }

        [Fact]
        public async Task PendingLocalUpload_PreventsOverwriteAndAllowsDirectRecovery()
        {
            var fakeDrive = new FakeDriveHttpHandler();
            var (svc, cacheRoot) = CreateTestService(fakeDrive);

            try
            {
                string projDir = Path.Combine(cacheRoot, "TowerProject");
                string centralDir = Path.Combine(projDir, "Central");
                Directory.CreateDirectory(centralDir);
                string centralFile = Path.Combine(centralDir, "TowerProject_Central.rvt");
                await File.WriteAllTextAsync(centralFile, "CentralContent_V1");

                // 1. Initial publish -> cloud is at v1
                await svc.CreateCloudProjectAsync("TowerProject", centralFile, "Filippo");

                // 2. Simulate Revit mutating local Central during sync, then upload fails -> pending_upload.json remains
                await File.WriteAllTextAsync(centralFile, "CentralContent_V2_UnuploadedLocalChanges");
                CentralSnapshotHelper.MarkPendingUpload(projDir, new PendingUploadMarker
                {
                    ProjectId = "TowerProject",
                    CentralPath = centralFile,
                    User = "Filippo",
                    ExpectedBaseVersion = 1,
                    LastError = "Simulated network outage"
                });

                // 3. VerifyIntegrityAndAlign MUST NOT overwrite local Central and MUST report PendingUploadCanRetryDirectly=true!
                var integrity = await svc.VerifyIntegrityAndAlignAsync("TowerProject", "Filippo");
                Assert.False(integrity.IsAligned);
                Assert.True(integrity.HasPendingLocalUpload);
                Assert.True(integrity.PendingUploadCanRetryDirectly);
                Assert.Equal("CentralContent_V2_UnuploadedLocalChanges", await File.ReadAllTextAsync(centralFile));

                // 4. Retry upload -> succeeds and clears pending_upload.json!
                await svc.UploadProjectChangesAsync("TowerProject", centralFile, "Filippo", expectedBaseVersion: 1);
                Assert.Null(CentralSnapshotHelper.ReadPendingUpload(projDir));
            }
            finally
            {
                if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
            }
        }

        [Fact]
        public async Task OptimisticConcurrency_BlocksOverwriteWhenTeammateAlreadyAdvancedVersion()
        {
            var fakeDrive = new FakeDriveHttpHandler();
            var (svcA, cacheA) = CreateTestService(fakeDrive);
            var (svcB, cacheB) = CreateTestService(fakeDrive);

            try
            {
                string projDirA = Path.Combine(cacheA, "BridgeProject");
                string centralA = Path.Combine(projDirA, "Central", "BridgeProject_Central.rvt");
                Directory.CreateDirectory(Path.GetDirectoryName(centralA)!);
                await File.WriteAllTextAsync(centralA, "V1");

                // User A publishes v1
                await svcA.CreateCloudProjectAsync("BridgeProject", centralA, "Alice");

                // User B downloads v1 and uploads v2
                string centralB = await svcB.DownloadAndPrepareProjectAsync("BridgeProject");
                await File.WriteAllTextAsync(centralB, "V2_ByBob");
                await svcB.UploadProjectChangesAsync("BridgeProject", centralB, "Bob", expectedBaseVersion: 1);

                // User A attempts to upload based on stale v1 -> MUST throw conflict error!
                await File.WriteAllTextAsync(centralA, "V2_StaleByAlice");
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    svcA.UploadProjectChangesAsync("BridgeProject", centralA, "Alice", expectedBaseVersion: 1));

                Assert.Contains("CONFLITTO RILEVATO", ex.Message);
            }
            finally
            {
                if (Directory.Exists(cacheA)) Directory.Delete(cacheA, true);
                if (Directory.Exists(cacheB)) Directory.Delete(cacheB, true);
            }
        }

        [Fact]
        public async Task UnifiedConnectorPoller_IsolatesSameRevitUsernameOnDifferentMachines()
        {
            var fakeDrive = new FakeDriveHttpHandler();
            var (svcA, cacheA) = CreateTestService(fakeDrive);
            var (svcB, cacheB) = CreateTestService(fakeDrive);

            try
            {
                var rootFolder = fakeDrive.AddFolder("RevitProjects");
                fakeDrive.AddFolder("VillaProject", rootFolder.Id);

                // Both teammates accidentally share the exact same Revit Username ("Architetto"), on different machines!
                await svcA.PublishLiveEditsAsync("VillaProject", "Architetto", "PC-ALFA", new List<string> { "uid-wall-001", "uid-door-002" });
                await svcB.PublishLiveEditsAsync("VillaProject", "Architetto", "PC-BETA", new List<string> { "uid-slab-999" });

                // Single unified poll by PC-BETA MUST see PC-ALFA's locked elements and NOT its own!
                var pollB = await svcB.PollConnectorStateAsync("VillaProject", "Architetto", "PC-BETA");
                Assert.Equal(2, pollB.RemoteLiveLocks.Count);
                Assert.Contains(pollB.RemoteLiveLocks, l => l.ElementUniqueId == "uid-wall-001" && l.OwnerMachine == "PC-ALFA");
                Assert.DoesNotContain(pollB.RemoteLiveLocks, l => l.ElementUniqueId == "uid-slab-999");

                // Single unified poll by PC-ALFA MUST see PC-BETA's locked element!
                var pollA = await svcA.PollConnectorStateAsync("VillaProject", "Architetto", "PC-ALFA");
                Assert.Single(pollA.RemoteLiveLocks);
                Assert.Equal("uid-slab-999", pollA.RemoteLiveLocks[0].ElementUniqueId);
                Assert.Equal("PC-BETA", pollA.RemoteLiveLocks[0].OwnerMachine);
            }
            finally
            {
                if (Directory.Exists(cacheA)) Directory.Delete(cacheA, true);
                if (Directory.Exists(cacheB)) Directory.Delete(cacheB, true);
            }
        }
    }
}
