using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services.Drive
{
    /// <summary>
    /// Handles cloud project discovery, central model sync (chunked upload/download with real progress),
    /// incremental hash validation (single-pass FileHashCache), optimistic conflict protection,
    /// Shared Drives support, and pending-upload anti-corruption protection.
    /// </summary>
    internal class CloudProjectSyncService
    {
        private static readonly HashSet<string> VolatileBackupFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "editingperms.dat",
            "editingdeltas.dat",
            "editingrequests.dat",
            "users.dat",
            "worksetperms.dat"
        };

        private readonly DriveApiClient _api;
        private readonly GoogleAuthService _auth;
        private readonly DriveFolderResolver _folders;
        private readonly CloudLockService _locks;
        private readonly CollaborationStateService _collab;
        private readonly Func<AppConfig> _configProvider;

        public CloudProjectSyncService(
            DriveApiClient api,
            GoogleAuthService auth,
            DriveFolderResolver folders,
            CloudLockService locks,
            CollaborationStateService collab,
            Func<AppConfig> configProvider)
        {
            _api = api;
            _auth = auth;
            _folders = folders;
            _locks = locks;
            _collab = collab;
            _configProvider = configProvider;
        }

        private sealed class TransferProgressTracker
        {
            private readonly Action<TransferProgressInfo>? _callback;
            private readonly string _phase;
            private readonly Stopwatch _sw = Stopwatch.StartNew();
            private readonly object _sync = new();
            private long _totalBytes;
            private long _transferredBytes;
            private int _totalFiles;
            private int _completedFiles;
            private string _currentFile = string.Empty;
            private long _lastEmitMs = -1000;

            public TransferProgressTracker(string phase, Action<TransferProgressInfo>? callback)
            {
                _phase = phase;
                _callback = callback;
            }

            public void SetTotals(int totalFiles, long totalBytes)
            {
                lock (_sync)
                {
                    _totalFiles = totalFiles;
                    _totalBytes = totalBytes;
                }
                Emit(force: true);
            }

            public void SetCurrentFile(string fileName)
            {
                lock (_sync)
                {
                    _currentFile = fileName;
                }
                Emit(force: true);
            }

            public void AddBytes(long delta)
            {
                if (_callback == null) return;
                lock (_sync)
                {
                    _transferredBytes = Math.Max(0, _transferredBytes + delta);
                }
                Emit(force: false);
            }

            public void MarkFileCompleted()
            {
                lock (_sync)
                {
                    _completedFiles++;
                }
                Emit(force: true);
            }

            private void Emit(bool force)
            {
                if (_callback == null) return;
                TransferProgressInfo snapshot;
                lock (_sync)
                {
                    long nowMs = _sw.ElapsedMilliseconds;
                    if (!force && (nowMs - _lastEmitMs) < 120) return;
                    _lastEmitMs = nowMs;

                    double elapsedSec = Math.Max(0.1, _sw.Elapsed.TotalSeconds);
                    snapshot = new TransferProgressInfo
                    {
                        Phase = _phase,
                        CurrentFileName = _currentFile,
                        CompletedFiles = _completedFiles,
                        TotalFiles = _totalFiles,
                        BytesTransferred = _transferredBytes,
                        TotalBytes = _totalBytes,
                        BytesPerSecond = _transferredBytes / elapsedSec
                    };
                }

                try { _callback(snapshot); } catch { }
            }
        }

        public async Task<List<CloudProjectItem>> ListProjectsAsync(CancellationToken ct = default)
        {
            var cfg = _configProvider();
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);

            var list = new List<CloudProjectItem>();
            await ScanLocalCacheDirectoryAsync(list, cfg.LocalCachePath, ct);

            if (!_auth.HasAuth)
            {
                return list.OrderBy(p => p.Name).ToList();
            }

            var seenFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var listLock = new object();
            using var scanSem = new SemaphoreSlim(6);

            try
            {
                // 1. Discover all accessible projects via manifest.json (covers My Drive, SharedWithMe, AND Google Workspace Shared Drives)
                var manifestFiles = await _api.ListFilesAsync(
                    "name = 'manifest.json' and trashed = false",
                    "id,name,parents,owners,lastModifyingUser,shared,driveId,modifiedTime",
                    200,
                    ct);

                var manifestTasks = manifestFiles
                    .Where(mf => mf.Parents != null && mf.Parents.Count > 0 && seenFolderIds.Add(mf.Parents[0]))
                    .Select(async mf =>
                    {
                        string parentFolderId = mf.Parents![0];
                        await scanSem.WaitAsync(ct);
                        try
                        {
                            var folderTask = _api.GetFileMetadataAsync(parentFolderId, "id,name,owners,lastModifyingUser,shared,driveId,modifiedTime", ct);
                            var manifestTask = TryDownloadManifestAsync(mf.Id, ct);
                            await Task.WhenAll(folderTask, manifestTask);

                            var projectFolder = folderTask.Result;
                            if (projectFolder == null) return;

                            lock (listLock)
                            {
                                MergeOrAddCloudProject(list, projectFolder, manifestTask.Result);
                            }
                        }
                        catch (Exception fEx)
                        {
                            AppLogger.Debug("CloudProjectSyncService", $"Lettura cartella cloud {parentFolderId} saltata: {fEx.Message}");
                        }
                        finally
                        {
                            scanSem.Release();
                        }
                    });
                await Task.WhenAll(manifestTasks);

                // 2. Discover shared folders without manifest yet (sharedWithMe = true)
                var sharedFolders = await _api.ListFilesAsync(
                    "sharedWithMe = true and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                    "id,name,owners,lastModifyingUser,shared,driveId,modifiedTime",
                    100,
                    ct);

                var sharedTasks = sharedFolders
                    .Where(folder => !seenFolderIds.Contains(folder.Id))
                    .Select(async folder =>
                    {
                        await scanSem.WaitAsync(ct);
                        try
                        {
                            var markers = await _api.ListFilesAsync(
                                $"'{folder.Id}' in parents and (name = 'manifest.json' or (name = 'Central' and mimeType = 'application/vnd.google-apps.folder')) and trashed = false",
                                "id,name,mimeType,modifiedTime",
                                10,
                                ct);

                            if (markers.Count == 0) return;

                            lock (listLock) { seenFolderIds.Add(folder.Id); }
                            var mf = markers
                                .Where(m => string.Equals(m.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(m => m.ModifiedTime ?? DateTimeOffset.MinValue)
                                .FirstOrDefault();

                            var cloudManifest = mf != null ? await TryDownloadManifestAsync(mf.Id, ct) : null;
                            lock (listLock)
                            {
                                MergeOrAddCloudProject(list, folder, cloudManifest, forceSharedWithMe: true);
                            }
                        }
                        catch (Exception sEx)
                        {
                            AppLogger.Debug("CloudProjectSyncService", $"Scansione cartella condivisa '{folder.Name}' saltata: {sEx.Message}");
                        }
                        finally
                        {
                            scanSem.Release();
                        }
                    });
                await Task.WhenAll(sharedTasks);

                // 3. Discover shortcuts/subfolders inside RevitProjects
                var revitProjectsFolders = await _api.ListFilesAsync(
                    "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                    "id,name",
                    10,
                    ct);

                foreach (var rpFolder in revitProjectsFolders)
                {
                    var children = await _api.ListFilesAsync(
                        $"'{rpFolder.Id}' in parents and (mimeType = 'application/vnd.google-apps.folder' or mimeType = 'application/vnd.google-apps.shortcut') and trashed = false",
                        "id,name,mimeType,shortcutDetails,owners,lastModifyingUser,shared,driveId,modifiedTime",
                        100,
                        ct);

                    var childTasks = children.Select(async child =>
                    {
                        string targetId = child.MimeType == "application/vnd.google-apps.shortcut"
                            ? (child.ShortcutDetails?.TargetId ?? "")
                            : child.Id;

                        if (string.IsNullOrEmpty(targetId)) return;
                        lock (listLock)
                        {
                            if (!seenFolderIds.Add(targetId)) return;
                        }

                        await scanSem.WaitAsync(ct);
                        try
                        {
                            var folderMeta = child.MimeType == "application/vnd.google-apps.shortcut"
                                ? await _api.GetFileMetadataAsync(targetId, "id,name,owners,lastModifyingUser,shared,driveId,modifiedTime", ct)
                                : child;

                            if (folderMeta == null) return;

                            var markers = await _api.ListFilesAsync(
                                $"'{targetId}' in parents and (name = 'manifest.json' or (name = 'Central' and mimeType = 'application/vnd.google-apps.folder')) and trashed = false",
                                "id,name,mimeType,modifiedTime",
                                10,
                                ct);

                            if (markers.Count == 0) return;

                            var mf = markers
                                .Where(m => string.Equals(m.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(m => m.ModifiedTime ?? DateTimeOffset.MinValue)
                                .FirstOrDefault();

                            var cloudManifest = mf != null ? await TryDownloadManifestAsync(mf.Id, ct) : null;
                            lock (listLock)
                            {
                                MergeOrAddCloudProject(list, folderMeta, cloudManifest);
                            }
                        }
                        catch { }
                        finally
                        {
                            scanSem.Release();
                        }
                    });
                    await Task.WhenAll(childTasks);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloudProjectSyncService", "Avviso scansione progetti Google Drive", ex);
            }

            return list.OrderBy(p => p.Name).ToList();
        }

        private async Task ScanLocalCacheDirectoryAsync(List<CloudProjectItem> list, string rootPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return;

            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                string pName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(pName) ||
                    pName.StartsWith(".") ||
                    pName.StartsWith("[") ||
                    string.Equals(pName, "Transition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string centralDir = Path.Combine(dir, "Central");
                if (!Directory.Exists(centralDir)) continue;

                string? centralRvt = FindCentralRvtFile(centralDir, pName);
                if (string.IsNullOrEmpty(centralRvt)) continue;

                string manifestPath = Path.Combine(dir, "manifest.json");
                long version = 1;
                DateTime lastMod = File.GetLastWriteTimeUtc(centralRvt);
                string lastBy = "Locale";
                string cloudFolderId = "";

                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var m = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(manifestPath, ct));
                        if (m != null)
                        {
                            version = m.Version;
                            lastMod = m.LastSyncUtc;
                            lastBy = m.LastSyncUser;
                            cloudFolderId = m.CloudFolderId ?? "";
                        }
                    }
                    catch { }
                }

                list.Add(new CloudProjectItem
                {
                    Id = pName,
                    Name = pName,
                    DriveFolderId = cloudFolderId,
                    CloudFolderId = cloudFolderId,
                    Version = version,
                    LastModifiedUtc = lastMod,
                    LastModifiedBy = lastBy,
                    LocalCachePath = dir,
                    CentralRvtName = Path.GetFileName(centralRvt),
                    IsMountedLocally = true,
                    SourceType = "MyDrive"
                });
            }
        }

        private void MergeOrAddCloudProject(
            List<CloudProjectItem> list,
            DriveFileItem projectFolder,
            ProjectManifest? cloudManifest,
            bool forceSharedWithMe = false)
        {
            string projectName = projectFolder.Name;
            if (string.IsNullOrWhiteSpace(projectName) ||
                projectName.Equals("RevitProjects", StringComparison.OrdinalIgnoreCase) ||
                projectName.Equals("Transition", StringComparison.OrdinalIgnoreCase) ||
                projectName.StartsWith("."))
            {
                return;
            }

            string folderId = projectFolder.Id;
            bool isSharedDrive = !string.IsNullOrEmpty(projectFolder.DriveId);
            var owner = projectFolder.Owners?.FirstOrDefault();
            bool isOwnedByMe = !isSharedDrive && (owner?.Me == true ||
                (!string.IsNullOrEmpty(_auth.ConnectedEmail) &&
                 string.Equals(owner?.EmailAddress, _auth.ConnectedEmail, StringComparison.OrdinalIgnoreCase)));

            bool isSharedWithMe = !isSharedDrive && (forceSharedWithMe || (!isOwnedByMe && (projectFolder.Shared == true || owner != null)));
            string sharedBy = isSharedDrive
                ? (projectFolder.LastModifyingUser?.DisplayName ?? "Workspace")
                : (owner?.DisplayName ?? owner?.EmailAddress ?? "Collaboratore");

            long version = cloudManifest?.Version ?? 1;
            DateTime modifiedUtc = cloudManifest?.LastSyncUtc ?? projectFolder.ModifiedTime?.UtcDateTime ?? DateTime.UtcNow;
            string modifiedBy = !string.IsNullOrEmpty(cloudManifest?.LastSyncUser)
                ? cloudManifest.LastSyncUser
                : (projectFolder.LastModifyingUser?.DisplayName ?? sharedBy);

            string sourceType = isSharedDrive ? "SharedDrive" : (isSharedWithMe ? "SharedWithMe" : "MyDrive");

            var existing = list.FirstOrDefault(x =>
                (!string.IsNullOrEmpty(x.CloudFolderId) && string.Equals(x.CloudFolderId, folderId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(x.Name, projectName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                bool incomingIsCanonical =
                    !string.IsNullOrEmpty(cloudManifest?.CloudFolderId) &&
                    string.Equals(folderId, cloudManifest.CloudFolderId, StringComparison.OrdinalIgnoreCase);
                bool existingIsCanonical =
                    !string.IsNullOrEmpty(existing.CloudFolderId) &&
                    !string.IsNullOrEmpty(cloudManifest?.CloudFolderId) &&
                    string.Equals(existing.CloudFolderId, cloudManifest.CloudFolderId, StringComparison.OrdinalIgnoreCase);

                bool incomingWins = false;
                if (string.IsNullOrEmpty(existing.CloudFolderId))
                {
                    incomingWins = true;
                }
                else if (cloudManifest != null)
                {
                    if (cloudManifest.Version > existing.Version)
                        incomingWins = true;
                    else if (cloudManifest.Version == existing.Version)
                    {
                        if (incomingIsCanonical && !existingIsCanonical) incomingWins = true;
                        else if (!incomingIsCanonical && existingIsCanonical) incomingWins = false;
                        else if (cloudManifest.LastSyncUtc > existing.LastModifiedUtc) incomingWins = true;
                        else if ((isSharedDrive || isSharedWithMe) && existing.SourceType == "MyDrive") incomingWins = true;
                    }
                }

                if (incomingWins)
                {
                    existing.CloudFolderId = folderId;
                    existing.DriveFolderId = folderId;
                    _folders.CacheProjectFolderId(projectName, folderId);
                    if (cloudManifest != null)
                    {
                        existing.Version = cloudManifest.Version;
                        existing.LastModifiedUtc = cloudManifest.LastSyncUtc;
                        if (!string.IsNullOrEmpty(cloudManifest.LastSyncUser))
                        {
                            existing.LastModifiedBy = cloudManifest.LastSyncUser;
                        }
                    }
                    existing.SourceType = sourceType;
                    existing.IsSharedWithMe = isSharedWithMe;
                    if (!string.IsNullOrEmpty(sharedBy)) existing.SharedBy = sharedBy;
                }
            }
            else
            {
                _folders.CacheProjectFolderId(projectName, folderId);
                list.Add(new CloudProjectItem
                {
                    Id = cloudManifest?.ProjectId ?? projectName,
                    Name = projectName,
                    DriveFolderId = folderId,
                    CloudFolderId = folderId,
                    Version = version,
                    LastModifiedUtc = modifiedUtc,
                    LastModifiedBy = modifiedBy,
                    IsSharedWithMe = isSharedWithMe,
                    SharedBy = sharedBy,
                    SourceType = sourceType,
                    IsMountedLocally = false
                });
            }
        }

        private async Task<ProjectManifest?> TryDownloadManifestAsync(string manifestFileId, CancellationToken ct)
        {
            try
            {
                using var ms = new MemoryStream();
                await _api.DownloadFileAsync(manifestFileId, ms, ct);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                return JsonSerializer.Deserialize<ProjectManifest>(json);
            }
            catch
            {
                return null;
            }
        }

        private async Task<(ProjectManifest? Manifest, string? RawJson)> TryGetCloudManifestFromFolderAsync(string cloudFolderId, CancellationToken ct)
        {
            var mFiles = await _api.ListFilesAsync(
                $"'{cloudFolderId}' in parents and name = 'manifest.json' and trashed = false",
                "id,name,modifiedTime",
                10,
                ct);

            var latestMf = mFiles
                .OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (latestMf == null) return (null, null);

            using var mStream = new MemoryStream();
            await _api.DownloadFileAsync(latestMf.Id, mStream, ct);
            string cloudJson = Encoding.UTF8.GetString(mStream.ToArray());
            return (JsonSerializer.Deserialize<ProjectManifest>(cloudJson), cloudJson);
        }

        public async Task<string> MountCloudProjectAsync(
            string projectName,
            string cloudFolderId,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            if (!string.IsNullOrWhiteSpace(cloudFolderId))
            {
                _folders.CacheProjectFolderId(projectName, cloudFolderId);
            }

            string localProjectDir = Path.Combine(_configProvider().LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            Directory.CreateDirectory(localCentralDir);

            // Optionally create a Drive shortcut inside user's RevitProjects for convenience
            if (!string.IsNullOrEmpty(cloudFolderId))
            {
                try
                {
                    var rpFolders = await _api.ListFilesAsync(
                        "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                        "id,name",
                        5,
                        ct);

                    string targetParentId;
                    if (rpFolders.Count > 0)
                    {
                        targetParentId = rpFolders[0].Id;
                    }
                    else
                    {
                        var createdRp = await _api.CreateMetadataOnlyAsync(new
                        {
                            name = "RevitProjects",
                            mimeType = "application/vnd.google-apps.folder"
                        }, ct);
                        targetParentId = createdRp.Id;
                    }

                    var existingRes = await _api.ListFilesAsync(
                        $"'{targetParentId}' in parents and name = '{projectName.Replace("'", "\\'")}' and trashed = false",
                        "id,name",
                        5,
                        ct);

                    if (existingRes.Count == 0)
                    {
                        await _api.CreateMetadataOnlyAsync(new
                        {
                            name = projectName,
                            mimeType = "application/vnd.google-apps.shortcut",
                            shortcutDetails = new { targetId = cloudFolderId },
                            parents = new[] { targetParentId }
                        }, ct);
                    }
                }
                catch { }

                await DownloadProjectFromCloudFolderAsync(cloudFolderId, localProjectDir, ct, progressCallback);
            }

            string? mountedCentral = FindCentralRvtFile(localCentralDir, projectName);
            if (!string.IsNullOrEmpty(mountedCentral))
            {
                return mountedCentral;
            }

            throw new FileNotFoundException($"Impossibile preparare il modello centrale per '{projectName}'.");
        }

        public async Task<string> DownloadAndPrepareProjectAsync(
            string projectName,
            string? preferredCloudFolderId,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);

            string localProjectDir = Path.Combine(_configProvider().LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            Directory.CreateDirectory(localCentralDir);

            string? cloudFolderId = !string.IsNullOrWhiteSpace(preferredCloudFolderId)
                ? preferredCloudFolderId
                : await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);

            if (string.IsNullOrEmpty(cloudFolderId))
            {
                throw new FileNotFoundException($"Progetto '{projectName}' non trovato su Google Drive.");
            }

            _folders.CacheProjectFolderId(projectName, cloudFolderId);
            AppLogger.Info("CloudProjectSyncService", $"Download e preparazione progetto '{projectName}' (folder={cloudFolderId})...");
            await DownloadProjectFromCloudFolderAsync(cloudFolderId, localProjectDir, ct, progressCallback);

            string? preparedCentral = FindCentralRvtFile(localCentralDir, projectName);
            if (string.IsNullOrEmpty(preparedCentral))
            {
                throw new FileNotFoundException($"Nessun modello centrale (.rvt) trovato per il progetto '{projectName}'.");
            }

            return preparedCentral;
        }

        private async Task DownloadProjectFromCloudFolderAsync(
            string cloudFolderId,
            string localProjectDir,
            CancellationToken ct,
            Action<TransferProgressInfo>? progressCallback)
        {
            FileHashCache.LoadProjectCache(localProjectDir);

            var (cloudManifest, cloudJson) = await TryGetCloudManifestFromFolderAsync(cloudFolderId, ct);

            // Follow canonical CloudFolderId if manifest points to a newer canonical folder
            if (cloudManifest != null &&
                !string.IsNullOrEmpty(cloudManifest.CloudFolderId) &&
                !string.Equals(cloudManifest.CloudFolderId, cloudFolderId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (canonicalManifest, canonicalJson) = await TryGetCloudManifestFromFolderAsync(cloudManifest.CloudFolderId, ct);
                    if (canonicalManifest != null && canonicalManifest.Version >= cloudManifest.Version)
                    {
                        cloudFolderId = cloudManifest.CloudFolderId;
                        cloudManifest = canonicalManifest;
                        cloudJson = canonicalJson;
                        if (!string.IsNullOrWhiteSpace(cloudManifest.ProjectName))
                        {
                            _folders.CacheProjectFolderId(cloudManifest.ProjectName, cloudFolderId);
                        }
                    }
                }
                catch { }
            }

            var allowedCentralPaths = BuildAllowedCentralPaths(cloudManifest);
            var tracker = new TransferProgressTracker("Download da Google Drive", progressCallback);
            await DownloadCloudFolderTreeAsync(cloudFolderId, localProjectDir, allowedCentralPaths, tracker, ct);

            if (!string.IsNullOrEmpty(cloudJson))
            {
                await File.WriteAllTextAsync(Path.Combine(localProjectDir, "manifest.json"), cloudJson, ct);
            }

            string localCentralDir = Path.Combine(localProjectDir, "Central");
            if (Directory.Exists(localCentralDir))
            {
                FileHashCache.SaveProjectCache(localProjectDir, Directory.GetFiles(localCentralDir, "*", SearchOption.AllDirectories));
            }
        }

        private async Task DownloadCloudFolderTreeAsync(
            string rootFolderId,
            string rootLocalDir,
            HashSet<string>? allowedCentralRelativePaths,
            TransferProgressTracker tracker,
            CancellationToken ct)
        {
            // Phase 1: Collect full download plan so we know exact TotalFiles & TotalBytes for accurate progress bar
            var filesToDownload = new List<(DriveFileItem Item, string LocalPath, bool IsInsideCentral)>();
            var expectedFilesPerCentralDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            await CollectDownloadPlanAsync(rootFolderId, rootLocalDir, rootLocalDir, isInsideCentral: false, allowedCentralRelativePaths, filesToDownload, expectedFilesPerCentralDir, ct);

            long totalBytes = filesToDownload.Sum(x => Math.Max(0, x.Item.Size ?? 0));
            tracker.SetTotals(filesToDownload.Count, totalBytes);

            // Phase 2: Download changed files in parallel (bounded concurrency = 6) with resumable Range downloads
            if (filesToDownload.Count > 0)
            {
                using var sem = new SemaphoreSlim(6);
                var tasks = filesToDownload.Select(async entry =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        tracker.SetCurrentFile(entry.Item.Name);
                        string tempDownload = entry.LocalPath + ".dl." + Guid.NewGuid().ToString("N");
                        try
                        {
                            using (var fileStream = new FileStream(tempDownload, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 256 * 1024, FileOptions.Asynchronous))
                            {
                                await _api.DownloadFileAsync(entry.Item.Id, fileStream, ct, delta => tracker.AddBytes(delta));
                            }
                            File.Move(tempDownload, entry.LocalPath, true);
                            FileHashCache.InvalidateFile(entry.LocalPath);
                            tracker.MarkFileCompleted();
                        }
                        finally
                        {
                            if (File.Exists(tempDownload))
                            {
                                try { File.Delete(tempDownload); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }

            // Phase 3: Clean stale local files inside Central that are no longer in cloud manifest
            foreach (var kvp in expectedFilesPerCentralDir)
            {
                if (!Directory.Exists(kvp.Key)) continue;
                foreach (var existingFile in Directory.GetFiles(kvp.Key))
                {
                    string fName = Path.GetFileName(existingFile);
                    if (fName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!kvp.Value.Contains(fName))
                    {
                        try
                        {
                            File.Delete(existingFile);
                            FileHashCache.InvalidateFile(existingFile);
                        }
                        catch { }
                    }
                }
            }
        }

        private async Task CollectDownloadPlanAsync(
            string folderId,
            string localDir,
            string projectRootDir,
            bool isInsideCentral,
            HashSet<string>? allowedCentralRelativePaths,
            List<(DriveFileItem Item, string LocalPath, bool IsInsideCentral)> filesToDownload,
            Dictionary<string, HashSet<string>> expectedFilesPerCentralDir,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(localDir);

            var files = await _api.ListFilesAsync(
                $"'{folderId}' in parents and trashed = false",
                "id,name,mimeType,size,md5Checksum,modifiedTime",
                1000,
                ct);

            var deduplicatedItems = files
                .Where(f => !IsIgnoredDirectoryName(f.Name) && !IsTransientTempFile(f.Name))
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).First())
                .ToList();

            foreach (var folder in deduplicatedItems.Where(f => f.MimeType == "application/vnd.google-apps.folder"))
            {
                ct.ThrowIfCancellationRequested();
                string childLocalDir = Path.Combine(localDir, folder.Name);
                bool childInCentral = isInsideCentral || string.Equals(folder.Name, "Central", StringComparison.OrdinalIgnoreCase);
                await CollectDownloadPlanAsync(folder.Id, childLocalDir, projectRootDir, childInCentral, allowedCentralRelativePaths, filesToDownload, expectedFilesPerCentralDir, ct);
            }

            var fileItems = deduplicatedItems.Where(f => f.MimeType != "application/vnd.google-apps.folder").ToList();
            if (isInsideCentral && allowedCentralRelativePaths != null)
            {
                fileItems = fileItems.Where(f =>
                {
                    string rel = Path.GetRelativePath(projectRootDir, Path.Combine(localDir, f.Name)).Replace('\\', '/');
                    return allowedCentralRelativePaths.Contains(rel);
                }).ToList();
            }

            if (isInsideCentral)
            {
                expectedFilesPerCentralDir[localDir] = new HashSet<string>(fileItems.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            }

            foreach (var item in fileItems)
            {
                ct.ThrowIfCancellationRequested();
                string localPath = Path.Combine(localDir, item.Name);

                if (File.Exists(localPath) && !string.Equals(item.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    var localFi = new FileInfo(localPath);
                    if (localFi.Length == (item.Size ?? -1) && !string.IsNullOrEmpty(item.Md5Checksum))
                    {
                        string localMd5 = await FileHashCache.GetOrComputeMd5Async(localPath, ct);
                        if (string.Equals(localMd5, item.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Already identical locally!
                        }
                    }
                }

                filesToDownload.Add((item, localPath, isInsideCentral));
            }
        }

        public async Task UploadProjectChangesAsync(
            string projectName,
            string localCentralFilePath,
            string user,
            long? expectedBaseVersion = null,
            string? expectedBaseHash = null,
            bool isInitialCreate = false,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            ct.ThrowIfCancellationRequested();
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: isInitialCreate, ct);

            string localCentralDir = Path.GetDirectoryName(localCentralFilePath)!;
            string localProjectDir = Path.GetDirectoryName(localCentralDir)!;
            FileHashCache.LoadProjectCache(localProjectDir);

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);

            string localManifestFile = Path.Combine(localProjectDir, "manifest.json");
            ProjectManifest? localOldManifest = null;
            long localOldVersion = 0;
            if (File.Exists(localManifestFile))
            {
                try
                {
                    localOldManifest = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(localManifestFile, ct));
                    if (localOldManifest != null) localOldVersion = localOldManifest.Version;
                }
                catch { }
            }

            ProjectManifest? cloudOldManifest = null;
            if (!string.IsNullOrEmpty(cloudFolderId))
            {
                (cloudOldManifest, _) = await TryGetCloudManifestFromFolderAsync(cloudFolderId, ct);

                if (isInitialCreate)
                {
                    if (cloudOldManifest != null)
                    {
                        throw new InvalidOperationException(
                            $"Esiste già un progetto cloud chiamato '{projectName}' su Google Drive (v{cloudOldManifest.Version}, ultimo agg. di {cloudOldManifest.LastSyncUser}).\nPer non sovrascrivere il lavoro del team, usa 'Apri da Google Drive' invece di 'Pubblica su Drive'.");
                    }

                    string? existingCloudCentralId = await _folders.GetSubfolderIdAsync(cloudFolderId, "Central", createIfMissing: false, ct);
                    if (!string.IsNullOrEmpty(existingCloudCentralId))
                    {
                        var existingCentralFiles = await _api.ListFilesAsync(
                            $"'{existingCloudCentralId}' in parents and trashed = false",
                            "id,name",
                            10,
                            ct);
                        if (existingCentralFiles.Any(f => f.Name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidOperationException(
                                $"Esiste già una cartella progetto '{projectName}' con modello centrale su Google Drive. Usa 'Apri da Google Drive' invece di 'Pubblica su Drive'.");
                        }
                    }
                }
            }

            // Optimistic concurrency check BEFORE mutating any cloud files
            if (!isInitialCreate)
            {
                if (cloudOldManifest != null)
                {
                    if (expectedBaseVersion.HasValue && cloudOldManifest.Version != expectedBaseVersion.Value)
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: il modello su Google Drive è stato aggiornato alla v{cloudOldManifest.Version} da '{cloudOldManifest.LastSyncUser}' mentre la tua operazione era basata sulla v{expectedBaseVersion.Value}. Caricamento annullato per non sovrascrivere le modifiche del collega.");
                    }

                    if (!expectedBaseVersion.HasValue && localOldVersion > 0 && cloudOldManifest.Version != localOldVersion)
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: il modello su Google Drive è alla v{cloudOldManifest.Version} (di '{cloudOldManifest.LastSyncUser}'), mentre la tua cache locale era alla v{localOldVersion}. Caricamento annullato per proteggere i dati su cloud.");
                    }

                    if (!string.IsNullOrEmpty(expectedBaseHash) &&
                        !string.IsNullOrEmpty(cloudOldManifest.CentralFileSha256) &&
                        !string.Equals(expectedBaseHash, cloudOldManifest.CentralFileSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: l'hash del modello centrale su Google Drive è cambiato rispetto alla verifica pre-sincronizzazione (ultimo agg. di '{cloudOldManifest.LastSyncUser}'). Caricamento annullato per evitare sovrascritture.");
                    }
                }
                else if (expectedBaseVersion.HasValue && expectedBaseVersion.Value > 0)
                {
                    throw new InvalidOperationException(
                        $"CONFLITTO RILEVATO: manifest.json non trovato su Google Drive (attesa v{expectedBaseVersion.Value}). Caricamento annullato per sicurezza.");
                }
            }

            if (string.IsNullOrEmpty(cloudFolderId))
            {
                cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: true, ct);
            }
            if (string.IsNullOrEmpty(cloudFolderId))
            {
                throw new InvalidOperationException($"Impossibile creare o accedere alla cartella '{projectName}' su Google Drive.");
            }

            // Single-pass incremental hashing for all files in Central (avoids double SHA-256 on 1 GB models!)
            progressCallback?.Invoke(new TransferProgressInfo { Phase = "Calcolo impronte file (hash incrementale)...", CurrentFileName = Path.GetFileName(localCentralFilePath) });

            var centralFiles = Directory.GetFiles(localCentralDir, "*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string relFromCentral = Path.GetRelativePath(localCentralDir, f);
                    string fileName = Path.GetFileName(f);
                    return !ContainsPathSegment(relFromCentral, "Revit_temp") && !IsTransientTempFile(fileName);
                })
                .OrderBy(f => Path.GetRelativePath(localCentralDir, f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fileHashMap = new Dictionary<string, (string Md5, string Sha256, FileInfo Info)>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in centralFiles)
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(f);
                var (md5, sha256) = await FileHashCache.GetOrComputeHashesAsync(f, ct);
                fileHashMap[f] = (md5, sha256, fi);
            }

            string centralHash = ComputeCentralStateHashFromPrecomputed(localCentralDir, localCentralFilePath, fileHashMap);
            var centralFileInfo = new FileInfo(localCentralFilePath);
            long cloudOldVersion = cloudOldManifest?.Version ?? 0;

            var manifest = new ProjectManifest
            {
                ProjectId = localOldManifest?.ProjectId ?? projectName,
                ProjectName = projectName,
                CloudFolderId = cloudFolderId,
                LastSyncUtc = DateTime.UtcNow,
                LastSyncUser = user,
                LastSyncMachine = Environment.MachineName,
                CentralFileSha256 = centralHash,
                CentralFileSizeBytes = centralFileInfo.Length,
                Version = Math.Max(localOldVersion, cloudOldVersion) + 1,
                ActiveUsers = await _collab.GetActiveUsersAsync(projectName, ct)
            };

            foreach (var f in centralFiles)
            {
                var (md5, sha256, fi) = fileHashMap[f];
                manifest.Files.Add(new FileEntry
                {
                    RelativePath = Path.GetRelativePath(localProjectDir, f),
                    Sha256 = sha256,
                    SizeBytes = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                });
            }

            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(localManifestFile, manifestJson, ct);
            FileHashCache.SaveProjectCache(localProjectDir, centralFiles);

            AppLogger.Info("CloudProjectSyncService", $"Avvio upload progetto '{projectName}' v{manifest.Version} su Drive ({cloudFolderId})...");
            var tracker = new TransferProgressTracker("Caricamento su Google Drive", progressCallback);
            await UploadCloudFolderTreeAsync(localProjectDir, cloudFolderId, fileHashMap, tracker, ct);

            // Only clear pending upload marker AFTER Drive has received all files + final manifest.json!
            CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
            AppLogger.Info("CloudProjectSyncService", $"Progetto '{projectName}' sincronizzato su Google Drive (v{manifest.Version}) da {user}.");
        }

        private async Task UploadCloudFolderTreeAsync(
            string rootLocalDir,
            string rootCloudFolderId,
            Dictionary<string, (string Md5, string Sha256, FileInfo Info)> precomputedHashes,
            TransferProgressTracker tracker,
            CancellationToken ct,
            bool? deleteMissingOverride = null)
        {
            var uploads = new List<UploadPlanItem>();
            var deletions = new List<(string FileId, string ParentFolderId)>();
            UploadPlanItem? deferredManifest = null;

            await CollectUploadPlanAsync(rootLocalDir, rootCloudFolderId, isInsideCentral: false, deleteMissingOverride, precomputedHashes, uploads, deletions, item => deferredManifest = item, ct);

            int totalFiles = uploads.Count + (deferredManifest != null ? 1 : 0);
            long totalBytes = uploads.Sum(u => u.FileSize) + (deferredManifest?.FileSize ?? 0);
            tracker.SetTotals(totalFiles, totalBytes);

            // Upload all data files concurrently (max 4 streams; large files use 8 MB resumable chunks)
            if (uploads.Count > 0)
            {
                using var sem = new SemaphoreSlim(4);
                var tasks = uploads.Select(async item =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        tracker.SetCurrentFile(item.FileName);
                        await ExecuteUploadItemAsync(item, tracker, ct);
                        tracker.MarkFileCompleted();
                    }
                    finally
                    {
                        sem.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }

            // Clean removed files inside Central
            foreach (var del in deletions)
            {
                ct.ThrowIfCancellationRequested();
                try { await _api.DeleteOrTrashFileAsync(del.FileId, del.ParentFolderId, ct); } catch { }
            }

            // Commit manifest.json LAST so teammates never observe a half-uploaded version!
            if (deferredManifest != null)
            {
                tracker.SetCurrentFile(deferredManifest.FileName);
                await ExecuteUploadItemAsync(deferredManifest, tracker, ct);
                tracker.MarkFileCompleted();
            }
        }

        private sealed class UploadPlanItem
        {
            public string LocalFilePath { get; init; } = string.Empty;
            public string FileName { get; init; } = string.Empty;
            public string ParentFolderId { get; init; } = string.Empty;
            public string? ExistingFileId { get; init; }
            public long FileSize { get; init; }
            public List<string> DuplicateIdsToRemove { get; init; } = new();
        }

        private async Task CollectUploadPlanAsync(
            string localDir,
            string parentFolderId,
            bool isInsideCentral,
            bool? deleteMissingOverride,
            Dictionary<string, (string Md5, string Sha256, FileInfo Info)> precomputedHashes,
            List<UploadPlanItem> uploads,
            List<(string FileId, string ParentFolderId)> deletions,
            Action<UploadPlanItem> setDeferredManifest,
            CancellationToken ct)
        {
            if (!Directory.Exists(localDir)) return;
            ct.ThrowIfCancellationRequested();

            bool deleteMissingFiles = deleteMissingOverride ?? isInsideCentral;

            var files = await _api.ListFilesAsync(
                $"'{parentFolderId}' in parents and trashed = false",
                "id,name,mimeType,size,md5Checksum,modifiedTime",
                1000,
                ct);

            var groupedByName = files
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var subDir in Directory.GetDirectories(localDir))
            {
                ct.ThrowIfCancellationRequested();
                string subName = Path.GetFileName(subDir);
                if (IsIgnoredDirectoryName(subName)) continue;
                localNames.Add(subName);

                string subFolderId;
                if (groupedByName.TryGetValue(subName, out var folderMatches) &&
                    folderMatches.FirstOrDefault(x => x.MimeType == "application/vnd.google-apps.folder") is DriveFileItem existingFolder)
                {
                    subFolderId = existingFolder.Id;
                }
                else
                {
                    var created = await _api.CreateMetadataOnlyAsync(new
                    {
                        name = subName,
                        mimeType = "application/vnd.google-apps.folder",
                        parents = new[] { parentFolderId }
                    }, ct);
                    subFolderId = created.Id;
                }

                bool childInCentral = isInsideCentral || string.Equals(subName, "Central", StringComparison.OrdinalIgnoreCase);
                await CollectUploadPlanAsync(subDir, subFolderId, childInCentral, deleteMissingOverride, precomputedHashes, uploads, deletions, setDeferredManifest, ct);
            }

            foreach (var localFile in Directory.GetFiles(localDir))
            {
                string fileName = Path.GetFileName(localFile);
                if (IsTransientTempFile(fileName) || IsLocalOnlyMetaFile(fileName)) continue;
                localNames.Add(fileName);

                var fi = new FileInfo(localFile);
                bool isDeferredManifest = !isInsideCentral && string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase);

                string? existingId = null;
                var dupIds = new List<string>();
                bool alreadyIdentical = false;

                if (groupedByName.TryGetValue(fileName, out var matches))
                {
                    var latest = matches[0];
                    existingId = latest.Id;
                    dupIds.AddRange(matches.Skip(1).Where(d => d.MimeType != "application/vnd.google-apps.folder").Select(d => d.Id));

                    if (!isDeferredManifest && (latest.Size ?? -1) == fi.Length && !string.IsNullOrEmpty(latest.Md5Checksum))
                    {
                        string localMd5 = precomputedHashes.TryGetValue(localFile, out var pre)
                            ? pre.Md5
                            : await FileHashCache.GetOrComputeMd5Async(localFile, ct);

                        if (string.Equals(localMd5, latest.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                        {
                            alreadyIdentical = true;
                        }
                    }
                }

                if (alreadyIdentical)
                {
                    foreach (var dup in dupIds) deletions.Add((dup, parentFolderId));
                    continue;
                }

                var planItem = new UploadPlanItem
                {
                    LocalFilePath = localFile,
                    FileName = fileName,
                    ParentFolderId = parentFolderId,
                    ExistingFileId = existingId,
                    FileSize = fi.Length,
                    DuplicateIdsToRemove = dupIds
                };

                if (isDeferredManifest)
                {
                    setDeferredManifest(planItem);
                }
                else
                {
                    uploads.Add(planItem);
                }
            }

            if (deleteMissingFiles)
            {
                foreach (var kvp in groupedByName)
                {
                    var latest = kvp.Value[0];
                    if (latest.MimeType != "application/vnd.google-apps.folder" && !localNames.Contains(kvp.Key))
                    {
                        foreach (var item in kvp.Value.Where(x => x.MimeType != "application/vnd.google-apps.folder"))
                        {
                            deletions.Add((item.Id, parentFolderId));
                        }
                    }
                }
            }
        }

        private async Task ExecuteUploadItemAsync(UploadPlanItem item, TransferProgressTracker tracker, CancellationToken ct)
        {
            using (var fs = new FileStream(item.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                if (!string.IsNullOrEmpty(item.ExistingFileId))
                {
                    await _api.UpdateFileContentAsync(item.ExistingFileId, fs, ct, delta => tracker.AddBytes(delta));
                }
                else
                {
                    await _api.CreateAndUploadFileAsync(item.FileName, item.ParentFolderId, fs, ct, delta => tracker.AddBytes(delta));
                }
            }

            foreach (var dupId in item.DuplicateIdsToRemove)
            {
                try { await _api.DeleteOrTrashFileAsync(dupId, item.ParentFolderId, ct); } catch { }
            }
        }

        public async Task UploadProjectLinksAsync(string projectName, string localLinksDir, CancellationToken ct = default)
        {
            if (!Directory.Exists(localLinksDir)) return;
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return;

            string? cloudLinksId = await _folders.GetSubfolderIdAsync(cloudFolderId, "Links", createIfMissing: true, ct);
            if (string.IsNullOrEmpty(cloudLinksId)) return;

            var tracker = new TransferProgressTracker("Caricamento Links su Drive", null);
            await UploadCloudFolderTreeAsync(
                localLinksDir,
                cloudLinksId,
                new Dictionary<string, (string, string, FileInfo)>(StringComparer.OrdinalIgnoreCase),
                tracker,
                ct,
                deleteMissingOverride: false);
        }

        public async Task CreateCloudProjectAsync(
            string projectName,
            string localCentralFilePath,
            string user,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await UploadProjectChangesAsync(
                projectName,
                localCentralFilePath,
                user,
                expectedBaseVersion: null,
                expectedBaseHash: null,
                isInitialCreate: true,
                ct: ct,
                progressCallback: progressCallback);
        }

        public async Task<SyncIntegrityResult> VerifyIntegrityAndAlignAsync(
            string projectName,
            string user,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            }
            catch (Exception authEx)
            {
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = false,
                    Message = authEx.Message
                };
            }

            string localProjectDir = Path.Combine(_configProvider().LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            string localManifestFile = Path.Combine(localProjectDir, "manifest.json");
            FileHashCache.LoadProjectCache(localProjectDir);

            long localVersion = 0;
            if (File.Exists(localManifestFile))
            {
                try
                {
                    var localManifest = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllTextAsync(localManifestFile, ct));
                    if (localManifest != null) localVersion = localManifest.Version;
                }
                catch { }
            }

            try
            {
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId))
                {
                    return new SyncIntegrityResult
                    {
                        IsAligned = false,
                        IsWaitingForDownload = false,
                        Message = $"Progetto '{projectName}' non trovato su Google Drive."
                    };
                }

                var (cloudManifest, cloudJson) = await TryGetCloudManifestFromFolderAsync(cloudFolderId, ct);

                // CRITICAL ANTI-CORRUPTION GUARD:
                // If a previous upload failed AFTER Revit updated the local Central cache, NEVER silently overwrite
                // Central from cloud (which would make the user's local .rvt incompatible with Central!).
                var pendingUpload = CentralSnapshotHelper.ReadPendingUpload(localProjectDir);
                if (pendingUpload != null)
                {
                    long cloudVer = cloudManifest?.Version ?? 0;
                    bool canRetryDirectly = !pendingUpload.ExpectedBaseVersion.HasValue || cloudVer == pendingUpload.ExpectedBaseVersion.Value;

                    AppLogger.Warn("CloudProjectSyncService", $"Rilevato pending_upload.json su '{projectName}' (base v{pendingUpload.ExpectedBaseVersion}, cloud v{cloudVer}, canRetry={canRetryDirectly}).");
                    return new SyncIntegrityResult
                    {
                        IsAligned = false,
                        IsWaitingForDownload = false,
                        HasPendingLocalUpload = true,
                        PendingUploadCanRetryDirectly = canRetryDirectly,
                        CloudVersion = cloudVer,
                        LastSyncUser = cloudManifest?.LastSyncUser ?? string.Empty,
                        ExpectedHash = cloudManifest?.CentralFileSha256 ?? string.Empty,
                        Message = canRetryDirectly
                            ? $"È presente una sincronizzazione locale precedente (da {pendingUpload.User}) non ancora caricata su Google Drive. Puoi riprendere subito l'upload senza perdere le tue modifiche."
                            : $"Attenzione: la tua ultima sincronizzazione locale (basata su v{pendingUpload.ExpectedBaseVersion}) non era stata caricata su Drive, e nel frattempo '{cloudManifest?.LastSyncUser}' ha pubblicato la v{cloudVer}. Evitato il sovrascrivere automatico per proteggere il tuo modello locale."
                    };
                }

                if (cloudManifest != null && !string.IsNullOrEmpty(cloudJson))
                {
                    string localHashBefore = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
                    if (!string.IsNullOrEmpty(cloudManifest.CentralFileSha256) &&
                        await CheckCentralHashMatchesAsync(localCentralDir, projectName, cloudManifest.CentralFileSha256, localHashBefore, ct) &&
                        localVersion == cloudManifest.Version)
                    {
                        return new SyncIntegrityResult
                        {
                            IsAligned = true,
                            WasUpdatedFromCloud = false,
                            CloudVersion = cloudManifest.Version,
                            LastSyncUser = cloudManifest.LastSyncUser,
                            ExpectedHash = cloudManifest.CentralFileSha256,
                            CurrentLocalHash = localHashBefore,
                            Message = "Modello locale già allineato all'ultima versione."
                        };
                    }

                    var allowedCentralPaths = BuildAllowedCentralPaths(cloudManifest);
                    var tracker = new TransferProgressTracker($"Allineamento v{cloudManifest.Version} da Google Drive", progressCallback);
                    await DownloadCloudFolderTreeAsync(cloudFolderId, localProjectDir, allowedCentralPaths, tracker, ct);
                    await File.WriteAllTextAsync(localManifestFile, cloudJson, ct);

                    if (Directory.Exists(localCentralDir))
                    {
                        FileHashCache.SaveProjectCache(localProjectDir, Directory.GetFiles(localCentralDir, "*", SearchOption.AllDirectories));
                    }

                    string localHashAfter = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
                    bool hashMatches = await CheckCentralHashMatchesAsync(localCentralDir, projectName, cloudManifest.CentralFileSha256, localHashAfter, ct);

                    return new SyncIntegrityResult
                    {
                        IsAligned = hashMatches,
                        IsWaitingForDownload = !hashMatches,
                        WasUpdatedFromCloud = hashMatches,
                        CloudVersion = cloudManifest.Version,
                        LastSyncUser = cloudManifest.LastSyncUser,
                        ExpectedHash = cloudManifest.CentralFileSha256,
                        CurrentLocalHash = localHashAfter,
                        Message = hashMatches
                            ? $"Allineamento completato con successo alla versione {cloudManifest.Version} di {cloudManifest.LastSyncUser}."
                            : "Verifica integrità modello centrale non riuscita dopo il download da Google Drive (hash non corrispondente)."
                    };
                }
                else
                {
                    string? cloudCentralId = await _folders.GetSubfolderIdAsync(cloudFolderId, "Central", createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudCentralId))
                    {
                        return new SyncIntegrityResult
                        {
                            IsAligned = false,
                            IsWaitingForDownload = false,
                            Message = $"Cartella 'Central' non trovata su Google Drive per '{projectName}'."
                        };
                    }

                    var tracker = new TransferProgressTracker("Allineamento da Google Drive", progressCallback);
                    await DownloadCloudFolderTreeAsync(cloudFolderId, localProjectDir, null, tracker, ct);
                    bool hasCentral = !string.IsNullOrEmpty(FindCentralRvtFile(localCentralDir, projectName));
                    return new SyncIntegrityResult
                    {
                        IsAligned = hasCentral,
                        IsWaitingForDownload = false,
                        WasUpdatedFromCloud = hasCentral,
                        CloudVersion = 0,
                        Message = hasCentral
                            ? "Modello centrale allineato da Google Drive (senza manifest precedente)."
                            : "Nessun file .rvt trovato nella cartella Central su Google Drive."
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception apiEx)
            {
                AppLogger.Error("CloudProjectSyncService", $"Errore controllo integrità API su '{projectName}'", apiEx);
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = false,
                    Message = $"Errore di connessione a Google Drive: {apiEx.Message}"
                };
            }
        }

        public async Task<CollaboratorOperationResult> AddCollaboratorAsync(
            string projectName,
            string email,
            string role = "writer",
            bool sendNotification = true,
            CancellationToken ct = default)
        {
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = "Account Google non collegato nel plugin. Accedi con Google da 'Account Google'."
                };
            }

            string? folderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId))
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Impossibile localizzare la cartella del progetto '{projectName}' su Google Drive."
                };
            }

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions?sendNotificationEmail={sendNotification.ToString().ToLowerInvariant()}&supportsAllDrives=true&fields=id,type,role,emailAddress,displayName,photoLink";
                string json = await _api.SendJsonRequestAsync(HttpMethod.Post, url, new
                {
                    type = "user",
                    role,
                    emailAddress = email
                }, ct);

                var created = JsonSerializer.Deserialize<DrivePermissionItem>(json) ?? new DrivePermissionItem();
                return new CollaboratorOperationResult
                {
                    Success = true,
                    Message = $"Progetto condiviso con {email} con successo!",
                    Collaborator = new CollaboratorItem
                    {
                        Id = created.Id,
                        Email = created.EmailAddress ?? email,
                        DisplayName = !string.IsNullOrEmpty(created.DisplayName) ? created.DisplayName : email,
                        Role = created.Role ?? role,
                        PhotoLink = created.PhotoLink
                    }
                };
            }
            catch (Exception ex)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Errore Google Drive durante l'aggiunta di {email}: {ex.Message}"
                };
            }
        }

        public async Task<List<CollaboratorItem>> ListCollaboratorsAsync(string projectName, CancellationToken ct = default)
        {
            var list = new List<CollaboratorItem>();
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return list;

            string? folderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId)) return list;

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions?supportsAllDrives=true&fields=permissions(id,type,role,emailAddress,displayName,photoLink)";
                string json = await _api.SendJsonRequestAsync(HttpMethod.Get, url, null, ct);
                var res = JsonSerializer.Deserialize<DrivePermissionListResponse>(json);

                if (res?.Permissions != null)
                {
                    foreach (var p in res.Permissions)
                    {
                        list.Add(new CollaboratorItem
                        {
                            Id = p.Id,
                            Email = p.EmailAddress ?? "Chiunque abbia il link",
                            DisplayName = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : (p.EmailAddress ?? p.Id),
                            Role = p.Role ?? "reader",
                            PhotoLink = p.PhotoLink
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloudProjectSyncService", $"Errore elenco collaboratori '{projectName}'", ex);
            }

            return list;
        }

        public async Task<CollaboratorOperationResult> RemoveCollaboratorAsync(string projectName, string permissionId, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth)
            {
                return new CollaboratorOperationResult { Success = false, Message = "Account Google non collegato." };
            }

            string? folderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId))
            {
                return new CollaboratorOperationResult { Success = false, Message = "Cartella progetto non trovata." };
            }

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions/{Uri.EscapeDataString(permissionId)}?supportsAllDrives=true";
                await _api.SendJsonRequestAsync(HttpMethod.Delete, url, null, ct);

                return new CollaboratorOperationResult
                {
                    Success = true,
                    Message = "Collaboratore rimosso con successo."
                };
            }
            catch (Exception ex)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Errore rimozione: {ex.Message}"
                };
            }
        }

        private static HashSet<string>? BuildAllowedCentralPaths(ProjectManifest? manifest)
        {
            if (manifest?.Files == null || manifest.Files.Count == 0) return null;

            var set = new HashSet<string>(
                manifest.Files
                    .Where(f => !string.IsNullOrWhiteSpace(f.RelativePath))
                    .Select(f => f.RelativePath.Replace('\\', '/'))
                    .Where(p => p.StartsWith("Central/", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            return set.Count > 0 ? set : null;
        }

        private static async Task<bool> CheckCentralHashMatchesAsync(
            string localCentralDir,
            string? projectName,
            string expectedHash,
            string computedStateHash,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(expectedHash))
            {
                return !string.IsNullOrEmpty(FindCentralRvtFile(localCentralDir, projectName));
            }

            if (string.Equals(computedStateHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Legacy manifest compatibility (raw .rvt SHA-256)
            string? rvtFile = FindCentralRvtFile(localCentralDir, projectName);
            if (!string.IsNullOrEmpty(rvtFile) && File.Exists(rvtFile))
            {
                string rawRvtSha = await FileHashCache.GetOrComputeSha256Async(rvtFile, ct);
                if (string.Equals(rawRvtSha, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string ComputeCentralStateHashFromPrecomputed(
            string centralDir,
            string centralRvtFile,
            Dictionary<string, (string Md5, string Sha256, FileInfo Info)> hashMap)
        {
            string expectedBackupDir = Path.Combine(
                centralDir,
                Path.GetFileNameWithoutExtension(centralRvtFile) + "_backup");

            string? backupDir = Directory.Exists(expectedBackupDir)
                ? expectedBackupDir
                : Directory.GetDirectories(centralDir, "*_Central_backup")
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .FirstOrDefault();

            if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
            {
                var versionedFiles = hashMap
                    .Where(kvp => kvp.Key.StartsWith(backupDir, StringComparison.OrdinalIgnoreCase))
                    .Where(kvp =>
                    {
                        string fileName = Path.GetFileName(kvp.Key);
                        if (VolatileBackupFiles.Contains(fileName)) return false;
                        string ext = Path.GetExtension(fileName);
                        return string.Equals(ext, ".rws", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(ext, ".dat", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(kvp => Path.GetRelativePath(backupDir, kvp.Key), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (versionedFiles.Count > 0)
                {
                    var rvtInfo = hashMap.TryGetValue(centralRvtFile, out var rvtEntry) ? rvtEntry.Info : new FileInfo(centralRvtFile);
                    var sb = new StringBuilder();
                    sb.Append("RVT:").Append(rvtInfo.Length).Append(';');

                    foreach (var kvp in versionedFiles)
                    {
                        string relPath = Path.GetRelativePath(backupDir, kvp.Key).Replace('\\', '/');
                        sb.Append(relPath).Append(':').Append(kvp.Value.Info.Length).Append(':').Append(kvp.Value.Md5).Append(';');
                    }

                    using var sha256 = SHA256.Create();
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    return Convert.ToHexString(hashBytes).ToLowerInvariant();
                }
            }

            return hashMap.TryGetValue(centralRvtFile, out var entry) ? entry.Sha256 : string.Empty;
        }

        internal static async Task<string> ComputeCentralStateHashAsync(string centralDir, string? projectName, CancellationToken ct)
        {
            string? centralRvtFile = FindCentralRvtFile(centralDir, projectName);
            if (string.IsNullOrEmpty(centralRvtFile) || !File.Exists(centralRvtFile))
            {
                return string.Empty;
            }

            try
            {
                string expectedBackupDir = Path.Combine(
                    centralDir,
                    Path.GetFileNameWithoutExtension(centralRvtFile) + "_backup");

                string? backupDir = Directory.Exists(expectedBackupDir)
                    ? expectedBackupDir
                    : Directory.GetDirectories(centralDir, "*_Central_backup")
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();

                if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
                {
                    var versionedFiles = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            string rel = Path.GetRelativePath(backupDir, f);
                            if (ContainsPathSegment(rel, "Revit_temp")) return false;

                            string fileName = Path.GetFileName(f);
                            if (VolatileBackupFiles.Contains(fileName)) return false;
                            if (IsTransientTempFile(fileName)) return false;

                            string ext = Path.GetExtension(fileName);
                            return string.Equals(ext, ".rws", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".dat", StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(f => Path.GetRelativePath(backupDir, f), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (versionedFiles.Count > 0)
                    {
                        var rvtInfo = new FileInfo(centralRvtFile);
                        var sb = new StringBuilder();
                        sb.Append("RVT:").Append(rvtInfo.Length).Append(';');

                        foreach (var f in versionedFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fi = new FileInfo(f);
                            string relPath = Path.GetRelativePath(backupDir, f).Replace('\\', '/');
                            string md5 = await FileHashCache.GetOrComputeMd5Async(f, ct);
                            sb.Append(relPath).Append(':').Append(fi.Length).Append(':').Append(md5).Append(';');
                        }

                        using var sha256 = SHA256.Create();
                        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                        return Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }
                }

                return await FileHashCache.GetOrComputeSha256Async(centralRvtFile, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloudProjectSyncService", $"Errore calcolo hash stato centrale in '{centralDir}'", ex);
                return string.Empty;
            }
        }

        private static bool IsIgnoredDirectoryName(string dirName)
        {
            return string.Equals(dirName, "Local", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Revit_temp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, ".connector", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Central_PreSyncSnapshot", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransientTempFile(string fileName)
        {
            return fileName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains(".dl.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocalOnlyMetaFile(string fileName)
        {
            return string.Equals(fileName, ".hashcache.json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fileName, "pending_upload.json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsPathSegment(string relPath, string segmentName)
        {
            var parts = relPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => string.Equals(p, segmentName, StringComparison.OrdinalIgnoreCase));
        }

        internal static string? FindCentralRvtFile(string centralDir, string? projectName = null)
        {
            if (!Directory.Exists(centralDir)) return null;

            if (!string.IsNullOrWhiteSpace(projectName))
            {
                string expectedPath = Path.Combine(centralDir, $"{projectName}_Central.rvt");
                if (File.Exists(expectedPath)) return expectedPath;
            }

            var centralCandidates = Directory.GetFiles(centralDir, "*_Central.rvt");
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                string expectedName = $"{projectName}_Central.rvt";
                var caseInsensitiveMatch = centralCandidates.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), expectedName, StringComparison.OrdinalIgnoreCase));
                if (caseInsensitiveMatch != null) return caseInsensitiveMatch;
            }

            if (centralCandidates.Length > 0)
            {
                return centralCandidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }

            var rvtCandidates = Directory.GetFiles(centralDir, "*.rvt");
            if (rvtCandidates.Length > 0)
            {
                return rvtCandidates.OrderByDescending(File.GetLastWriteTimeUtc).First();
            }

            return null;
        }

        public async Task<ProjectHistoryResponse> GetProjectHistoryAsync(string projectName, CancellationToken ct = default)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            string? projectFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(projectFolderId))
            {
                throw new DirectoryNotFoundException($"Cartella del progetto '{projectName}' non trovata su Google Drive.");
            }

            var (manifest, _) = await TryGetCloudManifestFromFolderAsync(projectFolderId, ct);
            long currentVersion = manifest?.Version ?? 1;

            string? centralFolderId = await _folders.GetSubfolderIdAsync(projectFolderId, "Central", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(centralFolderId))
            {
                return new ProjectHistoryResponse { ProjectId = projectName, CurrentVersion = currentVersion };
            }

            var rvtFiles = await _api.ListFilesAsync(
                $"'{centralFolderId}' in parents and name contains '.rvt' and mimeType != 'application/vnd.google-apps.folder' and trashed = false",
                "id,name,size,modifiedTime",
                50,
                ct);

            var mainRvt = rvtFiles.FirstOrDefault(f => f.Name.EndsWith("_Central.rvt", StringComparison.OrdinalIgnoreCase))
                          ?? rvtFiles.FirstOrDefault();

            var result = new ProjectHistoryResponse
            {
                ProjectId = projectName,
                CurrentVersion = currentVersion,
                Revisions = new List<ProjectRevisionInfo>()
            };

            if (mainRvt != null)
            {
                var revisions = await _api.ListRevisionsAsync(mainRvt.Id, ct);
                long revIdx = 1;
                foreach (var r in revisions.OrderBy(x => x.ModifiedTime ?? DateTimeOffset.MinValue))
                {
                    result.Revisions.Add(new ProjectRevisionInfo
                    {
                        RevisionId = r.Id,
                        ModifiedTimeUtc = (r.ModifiedTime ?? DateTimeOffset.UtcNow).UtcDateTime,
                        SizeBytes = r.Size ?? 0,
                        AuthorName = r.LastModifyingUser?.DisplayName ?? "Collaboratore",
                        AuthorEmail = r.LastModifyingUser?.EmailAddress ?? "",
                        Version = revIdx++,
                        Description = $"Revisione Google Drive ({r.ModifiedTime?.ToLocalTime():yyyy-MM-dd HH:mm})"
                    });
                }
            }

            return result;
        }

        public async Task<RollbackResponse> RollbackProjectAsync(
            string projectName,
            string revisionId,
            string user,
            string machine,
            string reason,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            string? projectFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(projectFolderId))
            {
                throw new DirectoryNotFoundException($"Cartella del progetto '{projectName}' non trovata su Google Drive.");
            }

            // 1. Acquire exclusive lock for rollback
            var lockResp = await _locks.AcquireProjectLockAsync(new LockRequest
            {
                ProjectId = projectName,
                User = user,
                Machine = machine,
                TimeoutSeconds = 300
            }, ct);

            if (lockResp == null || lockResp.Status != SyncLockStatus.LockAcquired || string.IsNullOrEmpty(lockResp.LockToken))
            {
                throw new InvalidOperationException($"Impossibile acquisire il lock per il rollback: {lockResp?.Message ?? "progetto occupato"}.");
            }

            try
            {
                string? centralFolderId = await _folders.GetSubfolderIdAsync(projectFolderId, "Central", createIfMissing: false, ct);
                if (string.IsNullOrEmpty(centralFolderId))
                {
                    throw new DirectoryNotFoundException("Cartella Central non trovata nel progetto su Google Drive.");
                }

                var rvtFiles = await _api.ListFilesAsync(
                    $"'{centralFolderId}' in parents and name contains '.rvt' and mimeType != 'application/vnd.google-apps.folder' and trashed = false",
                    "id,name,size,modifiedTime",
                    50,
                    ct);

                var mainRvt = rvtFiles.FirstOrDefault(f => f.Name.EndsWith("_Central.rvt", StringComparison.OrdinalIgnoreCase))
                              ?? rvtFiles.FirstOrDefault();

                if (mainRvt == null)
                {
                    throw new FileNotFoundException("Modello centrale .rvt non trovato nella cartella Central su Google Drive.");
                }

                // 2. Download historical revision to a temp file
                string tempDir = Path.Combine(AppConfig.Load().LocalCachePath, projectName, ".snapshots", "rollback_staging");
                Directory.CreateDirectory(tempDir);
                string tempRvt = Path.Combine(tempDir, mainRvt.Name);

                var progress = new TransferProgressTracker("Scaricamento revisione storica", progressCallback);
                progress.SetTotals(1, mainRvt.Size ?? 100 * 1024 * 1024);
                progress.SetCurrentFile(mainRvt.Name);

                using (var fs = new FileStream(tempRvt, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await _api.DownloadRevisionAsync(mainRvt.Id, revisionId, fs, ct, progress.AddBytes);
                }
                progress.MarkFileCompleted();

                // 3. Update central .rvt content with the historical content
                var uploadProgress = new TransferProgressTracker("Ripristino revisione su Google Drive", progressCallback);
                uploadProgress.SetTotals(1, new FileInfo(tempRvt).Length);
                uploadProgress.SetCurrentFile(mainRvt.Name);

                using (var uploadStream = new FileStream(tempRvt, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await _api.UpdateFileContentAsync(mainRvt.Id, uploadStream, ct, uploadProgress.AddBytes);
                }
                uploadProgress.MarkFileCompleted();

                // 4. Update manifest.json incrementing version
                var (currentManifest, _) = await TryGetCloudManifestFromFolderAsync(projectFolderId, ct);
                long oldVer = currentManifest?.Version ?? 1;
                long newVer = oldVer + 1;

                var updatedManifest = currentManifest ?? new ProjectManifest
                {
                    ProjectId = projectName,
                    ProjectName = projectName,
                    CentralRvtName = mainRvt.Name
                };
                updatedManifest.Version = newVer;
                updatedManifest.CentralRvtName = mainRvt.Name;
                updatedManifest.LastSyncUtc = DateTime.UtcNow;
                updatedManifest.LastSyncUser = $"{user} (Rollback rev {revisionId})";

                var mFiles = await _api.ListFilesAsync(
                    $"'{projectFolderId}' in parents and name = 'manifest.json' and trashed = false",
                    "id,name",
                    5,
                    ct);
                var mfItem = mFiles.FirstOrDefault();

                byte[] mfBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(updatedManifest, new JsonSerializerOptions { WriteIndented = true }));
                using (var mfMs = new MemoryStream(mfBytes))
                {
                    if (mfItem != null)
                    {
                        await _api.UpdateFileContentAsync(mfItem.Id, mfMs, ct);
                    }
                    else
                    {
                        await _api.CreateAndUploadFileAsync("manifest.json", projectFolderId, mfMs, ct);
                    }
                }

                // Also update local Central cache and manifest
                string localProjectDir = Path.Combine(AppConfig.Load().LocalCachePath, projectName);
                string localCentralRvt = Path.Combine(localProjectDir, "Central", mainRvt.Name);
                if (File.Exists(localCentralRvt))
                {
                    File.Copy(tempRvt, localCentralRvt, true);
                }
                string localMf = Path.Combine(localProjectDir, "manifest.json");
                await File.WriteAllBytesAsync(localMf, mfBytes, ct);

                try { File.Delete(tempRvt); } catch { }

                AppLogger.Info("CloudProjectSyncService", $"Rollback completato su '{projectName}': ripristinata revisione {revisionId} come nuova versione {newVer}.");

                return new RollbackResponse
                {
                    Success = true,
                    RestoredVersion = oldVer,
                    NewVersion = newVer,
                    Message = $"Rollback completato con successo: ripristinata la revisione storica e creata la nuova versione v{newVer} su Google Drive."
                };
            }
            finally
            {
                await _locks.ReleaseProjectLockAsync(projectName, lockResp.LockToken, user, ct);
            }
        }

        public async Task<CheckCloudLinksResponse> CheckCloudLinksAsync(string projectName, string localProjectDir, CancellationToken ct = default)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            string? projectFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(projectFolderId))
            {
                return new CheckCloudLinksResponse { ProjectId = projectName, UpdatesAvailable = false };
            }

            string? linksFolderId = await _folders.GetSubfolderIdAsync(projectFolderId, "Links", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(linksFolderId))
            {
                return new CheckCloudLinksResponse { ProjectId = projectName, UpdatesAvailable = false };
            }

            var cloudFiles = await _api.ListFilesAsync(
                $"'{linksFolderId}' in parents and mimeType != 'application/vnd.google-apps.folder' and trashed = false",
                "id,name,size,modifiedTime,md5Checksum",
                100,
                ct);

            string localLinksDir = Path.Combine(localProjectDir, "Links");
            var items = new List<CloudLinkUpdateItem>();
            bool anyUpdates = false;

            foreach (var cf in cloudFiles)
            {
                string localPath = Path.Combine(localLinksDir, cf.Name);
                bool existsLocally = File.Exists(localPath);
                long localSize = existsLocally ? new FileInfo(localPath).Length : 0;
                DateTime localModUtc = existsLocally ? File.GetLastWriteTimeUtc(localPath) : DateTime.MinValue;
                DateTime cloudModUtc = (cf.ModifiedTime ?? DateTimeOffset.UtcNow).UtcDateTime;

                bool isNewer = false;
                if (!existsLocally)
                {
                    isNewer = true;
                }
                else if (cf.Size.HasValue && cf.Size.Value != localSize)
                {
                    isNewer = true;
                }
                else if (cloudModUtc > localModUtc.AddSeconds(5))
                {
                    isNewer = true;
                }

                if (isNewer) anyUpdates = true;

                items.Add(new CloudLinkUpdateItem
                {
                    FileName = cf.Name,
                    CloudFileId = cf.Id,
                    CloudSizeBytes = cf.Size ?? 0,
                    CloudModifiedTimeUtc = cloudModUtc,
                    LocalExists = existsLocally,
                    LocalSizeBytes = localSize,
                    LocalModifiedTimeUtc = localModUtc,
                    HasNewCloudVersion = isNewer
                });
            }

            return new CheckCloudLinksResponse
            {
                ProjectId = projectName,
                UpdatesAvailable = anyUpdates,
                Links = items
            };
        }

        public async Task<bool> DownloadCloudLinkAsync(
            string projectName,
            string fileName,
            string localProjectDir,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            string? projectFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(projectFolderId)) return false;

            string? linksFolderId = await _folders.GetSubfolderIdAsync(projectFolderId, "Links", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(linksFolderId)) return false;

            var files = await _api.ListFilesAsync(
                $"'{linksFolderId}' in parents and name = '{fileName.Replace("'", "\\'")}' and trashed = false",
                "id,name,size,modifiedTime",
                5,
                ct);

            var linkFile = files.FirstOrDefault();
            if (linkFile == null) return false;

            string localLinksDir = Path.Combine(localProjectDir, "Links");
            Directory.CreateDirectory(localLinksDir);
            string targetPath = Path.Combine(localLinksDir, fileName);

            var progress = new TransferProgressTracker($"Scaricamento collegamento {fileName}", progressCallback);
            progress.SetTotals(1, linkFile.Size ?? 50 * 1024 * 1024);
            progress.SetCurrentFile(fileName);

            string tempFile = targetPath + ".download.tmp";
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _api.DownloadFileAsync(linkFile.Id, fs, ct, progress.AddBytes);
            }
            progress.MarkFileCompleted();

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(tempFile, targetPath);

            if (linkFile.ModifiedTime.HasValue)
            {
                File.SetLastWriteTimeUtc(targetPath, linkFile.ModifiedTime.Value.UtcDateTime);
            }

            AppLogger.Info("CloudProjectSyncService", $"Collegamento '{fileName}' scaricato con successo in '{targetPath}'.");
            return true;
        }
    }
}
