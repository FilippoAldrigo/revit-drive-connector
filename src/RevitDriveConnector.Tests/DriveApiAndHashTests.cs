using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Services.Drive;
using RevitDriveConnector.Core.Utils;
using Xunit;

namespace RevitDriveConnector.Tests
{
    public class DriveApiAndHashTests
    {
        [Fact]
        public async Task ChunkedResumableUpload_RecoversFromMidUpload503AndPreservesExactBytes()
        {
            var fakeDrive = new FakeDriveHttpHandler { FailNextChunkPutCount = 1 };
            var folder = fakeDrive.AddFolder("Central");
            var http = new HttpClient(fakeDrive);
            var auth = new GoogleAuthService(http, useFixedConfig: true, customTokenProvider: _ => Task.FromResult("test-token"));
            // Use 256 KB chunk size to force 5 chunks for a 1.15 MB payload
            var api = new DriveApiClient(http, auth, () => 256 * 1024);

            byte[] payload = new byte[1_150_000];
            new Random(42).NextBytes(payload);

            long reportedBytes = 0;
            using var ms = new MemoryStream(payload);
            var created = await api.CreateAndUploadFileAsync("BigModel_Central.rvt", folder.Id, ms, default, delta => reportedBytes += delta);

            Assert.NotNull(created);
            Assert.Equal(payload.Length, reportedBytes);
            // 5 chunks + 1 failed attempt on chunk #3 = 6 total PUTs
            Assert.True(fakeDrive.TotalChunkPuts >= 6);

            var stored = fakeDrive.GetAllFiles().Single(f => f.Id == created!.Id);
            Assert.Equal(payload, stored.Content);
        }

        [Fact]
        public async Task ListFiles_RetriesAutomaticallyOn429RateLimit()
        {
            var fakeDrive = new FakeDriveHttpHandler { FailNextListWith429Count = 2 };
            fakeDrive.AddFolder("ProjectAlpha");
            var http = new HttpClient(fakeDrive);
            var auth = new GoogleAuthService(http, useFixedConfig: true, customTokenProvider: _ => Task.FromResult("test-token"));
            var api = new DriveApiClient(http, auth);

            var files = await api.ListFilesAsync("name = 'ProjectAlpha' and trashed = false", "id,name");

            Assert.Single(files);
            Assert.Equal("ProjectAlpha", files[0].Name);
            Assert.Equal(3, fakeDrive.TotalListCalls);
        }

        [Fact]
        public async Task DeleteOrTrashFile_FallsBackToSoftTrashOnSharedDrive403()
        {
            var fakeDrive = new FakeDriveHttpHandler { SimulateSharedDriveDeleteForbidden = true };
            var folder = fakeDrive.AddFolder("SharedProject", driveId: "0AD_WORKSPACE_SHARED");
            var file = fakeDrive.AddFile("stale_lock.json", folder.Id, new byte[] { 1, 2, 3 });
            var http = new HttpClient(fakeDrive);
            var auth = new GoogleAuthService(http, useFixedConfig: true, customTokenProvider: _ => Task.FromResult("test-token"));
            var api = new DriveApiClient(http, auth);

            await api.DeleteOrTrashFileAsync(file.Id, folder.Id);

            Assert.Equal(1, fakeDrive.TotalTrashPatches);
            Assert.True(file.Trashed);
        }

        [Fact]
        public async Task FileHashCache_ComputesAccurateSinglePassHashesAndInvalidatesOnChange()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "RdcTestHash_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string filePath = Path.Combine(tempDir, "model.dat");
                await File.WriteAllBytesAsync(filePath, System.Text.Encoding.UTF8.GetBytes("RevitDeltaVersion1"));

                var (md5, sha256) = await FileHashCache.GetOrComputeHashesAsync(filePath);
                string refMd5 = await FileHashHelper.ComputeMd5Async(filePath);
                string refSha = await FileHashHelper.ComputeSha256Async(filePath);

                Assert.Equal(refMd5, md5);
                Assert.Equal(refSha, sha256);

                // Persist and reload cache
                FileHashCache.SaveProjectCache(tempDir, new[] { filePath });
                FileHashCache.ClearAll();
                FileHashCache.LoadProjectCache(tempDir);
                var (cachedMd5, cachedSha) = await FileHashCache.GetOrComputeHashesAsync(filePath);
                Assert.Equal(refMd5, cachedMd5);
                Assert.Equal(refSha, cachedSha);

                // Modify file -> cache must recompute automatically
                await File.WriteAllBytesAsync(filePath, System.Text.Encoding.UTF8.GetBytes("RevitDeltaVersion2_Modified"));
                var (newMd5, newSha) = await FileHashCache.GetOrComputeHashesAsync(filePath);
                Assert.NotEqual(refMd5, newMd5);
                Assert.NotEqual(refSha, newSha);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }
    }
}
