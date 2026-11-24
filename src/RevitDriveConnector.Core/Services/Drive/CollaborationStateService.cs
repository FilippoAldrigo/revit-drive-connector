using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services.Drive
{
    /// <summary>
    /// Unified serverless collaboration state service for `.connector`:
    /// - Single-API-call unified polling for Live Element Locks, Borrow Requests, and Sync Lock status
    ///   (reduces Google Drive API calls by ~3x for teams of 10+ concurrent profiles)
    /// - Unique client identity (user + machine) so teammates with identical Revit usernames never collide
    /// - Active user session tracking & Borrow Request persistence
    /// </summary>
    internal class CollaborationStateService
    {
        private readonly DriveApiClient _api;
        private readonly GoogleAuthService _auth;
        private readonly DriveFolderResolver _folders;
        private readonly CloudLockService _lockService;
        private readonly Func<AppConfig> _configProvider;

        private readonly ConcurrentDictionary<string, string> _liveEditsUploadFileIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (DateTimeOffset? ModifiedTime, UserLiveEditsState State)> _remoteLiveEditsCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (DateTimeOffset? ModifiedTime, string Md5)> _borrowRequestsMetaCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _borrowFileIdCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _sessionsFileIdCache = new(StringComparer.OrdinalIgnoreCase);

        public CollaborationStateService(
            DriveApiClient api,
            GoogleAuthService auth,
            DriveFolderResolver folders,
            CloudLockService lockService,
            Func<AppConfig> configProvider)
        {
            _api = api;
            _auth = auth;
            _folders = folders;
            _lockService = lockService;
            _configProvider = configProvider;
            _auth.OnAccountDisconnected += ClearCaches;
        }

        public void ClearCaches()
        {
            _liveEditsUploadFileIds.Clear();
            _remoteLiveEditsCache.Clear();
            _borrowRequestsMetaCache.Clear();
            _borrowFileIdCache.Clear();
            _sessionsFileIdCache.Clear();
        }

        public static string BuildClientId(string user, string machine)
        {
            string u = string.IsNullOrWhiteSpace(user) ? "unknown" : user.Trim();
            string m = string.IsNullOrWhiteSpace(machine) ? Environment.MachineName : machine.Trim();
            return $"{u}@{m}";
        }

        public static string SanitizeFileToken(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) || c == '@' ? '_' : c);
            }
            return sb.ToString();
        }

        public string GetLocalProjectDir(string projectName)
            => Path.Combine(_configProvider().LocalCachePath, projectName);

        /// <summary>
        /// Performs a single `files.list` on `.connector` and returns:
        /// - All active remote live element locks (downloading only files whose modifiedTime changed)
        /// - Pending & outgoing borrow requests (downloading borrow_requests.json only if modifiedTime/MD5 changed)
        /// - Current active sync lock (if any sync_lock_*.json is present)
        /// </summary>
        public async Task<ConnectorPollSnapshot> PollConnectorStateAsync(
            string projectName,
            string currentUser,
            string currentMachine,
            CancellationToken ct = default)
        {
            var snapshot = new ConnectorPollSnapshot();
            if (string.IsNullOrWhiteSpace(projectName)) return snapshot;

            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return snapshot;

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return snapshot;

            string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudConnectorId)) return snapshot;

            try
            {
                var items = await _api.ListFilesAsync(
                    $"'{cloudConnectorId}' in parents and trashed = false",
                    "id,name,mimeType,md5Checksum,createdTime,modifiedTime",
                    100,
                    ct);

                var nowUtc = NetworkTimeHelper.GetAccurateUtc();
                string currentClientId = BuildClientId(currentUser, currentMachine);
                string ownFileName = $"live_edits_{SanitizeFileToken(currentUser)}_{SanitizeFileToken(currentMachine)}.json";

                // 1. Remote live element edits (metadata-cached)
                var liveEditItems = items.Where(f =>
                    f.MimeType != "application/vnd.google-apps.folder" &&
                    f.Name.StartsWith("live_edits_", StringComparison.OrdinalIgnoreCase) &&
                    f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(f.Name, ownFileName, StringComparison.OrdinalIgnoreCase)).ToList();

                var states = new List<UserLiveEditsState>();
                using (var sem = new SemaphoreSlim(4))
                {
                    var lockObj = new object();
                    var tasks = liveEditItems.Select(async item =>
                    {
                        UserLiveEditsState? state = null;
                        if (_remoteLiveEditsCache.TryGetValue(item.Id, out var cached) &&
                            cached.ModifiedTime.HasValue &&
                            item.ModifiedTime.HasValue &&
                            cached.ModifiedTime.Value == item.ModifiedTime.Value)
                        {
                            state = cached.State;
                        }
                        else
                        {
                            await sem.WaitAsync(ct);
                            try
                            {
                                using var ms = new MemoryStream();
                                await _api.DownloadFileAsync(item.Id, ms, ct);
                                string json = Encoding.UTF8.GetString(ms.ToArray());
                                state = JsonSerializer.Deserialize<UserLiveEditsState>(json);
                                if (state != null)
                                {
                                    _remoteLiveEditsCache[item.Id] = (item.ModifiedTime, state);
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Debug("CollaborationStateService", $"Lettura live_edits '{item.Name}' saltata: {ex.Message}");
                            }
                            finally
                            {
                                sem.Release();
                            }
                        }

                        if (state != null)
                        {
                            lock (lockObj) { states.Add(state); }
                        }
                    });
                    await Task.WhenAll(tasks);
                }

                MergeRemoteLiveEditStates(states, currentClientId, currentUser, currentMachine, nowUtc, ttlMinutes: 15.0, snapshot.RemoteLiveLocks);

                // 2. Borrow requests (metadata-cached)
                string localProjectDir = GetLocalProjectDir(projectName);
                var borrowItem = items
                    .Where(f => f.MimeType != "application/vnd.google-apps.folder" &&
                                string.Equals(f.Name, "borrow_requests.json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.ModifiedTime ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();

                if (borrowItem != null)
                {
                    _borrowFileIdCache[projectName] = borrowItem.Id;
                    await SyncBorrowItemToLocalCacheAsync(projectName, localProjectDir, borrowItem, ct);
                }

                if (!string.IsNullOrWhiteSpace(currentUser))
                {
                    snapshot.PendingBorrowRequests = BorrowRequestManager.GetPendingRequestsForUser(localProjectDir, currentUser);
                    snapshot.OutgoingBorrowRequests = BorrowRequestManager.GetOutgoingRequestsForUser(localProjectDir, currentUser);
                }

                // 3. Active sync lock check (only downloads if a sync_lock file exists!)
                var lockItems = items.Where(f => f.MimeType != "application/vnd.google-apps.folder" && CloudLockService.IsSyncLockFileName(f.Name)).ToList();
                if (lockItems.Count > 0)
                {
                    var activeLocks = await _lockService.ReadCloudLocksFromItemsAsync(
                        cloudConnectorId,
                        lockItems,
                        nowUtc,
                        _configProvider().SyncTimeoutSeconds,
                        deleteExpired: false,
                        ct);
                    if (activeLocks.Count > 0)
                    {
                        snapshot.ActiveSyncLock = activeLocks
                            .OrderBy(x => x.driveItem.CreatedTime ?? x.driveItem.ModifiedTime ?? DateTimeOffset.MaxValue)
                            .First()
                            .data;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"Errore PollConnectorStateAsync su '{projectName}'", ex);
                _folders.InvalidateSubfolder(cloudFolderId, ".connector");
            }

            return snapshot;
        }

        private async Task SyncBorrowItemToLocalCacheAsync(string projectName, string localProjectDir, DriveFileItem latest, CancellationToken ct)
        {
            string connectorDir = Path.Combine(localProjectDir, ".connector");
            Directory.CreateDirectory(connectorDir);
            string targetFile = Path.Combine(connectorDir, "borrow_requests.json");

            bool unchanged = File.Exists(targetFile) &&
                             _borrowRequestsMetaCache.TryGetValue(projectName, out var cached) &&
                             cached.ModifiedTime == latest.ModifiedTime &&
                             string.Equals(cached.Md5, latest.Md5Checksum ?? "", StringComparison.OrdinalIgnoreCase);

            if (unchanged) return;

            string temp = targetFile + ".dl." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await _api.DownloadFileAsync(latest.Id, fs, ct);
                }
                File.Move(temp, targetFile, true);
                _borrowRequestsMetaCache[projectName] = (latest.ModifiedTime, latest.Md5Checksum ?? "");
            }
            finally
            {
                if (File.Exists(temp)) try { File.Delete(temp); } catch { }
            }
        }

        public async Task PublishLiveEditsAsync(
            string projectName,
            string user,
            string machine,
            List<string> elementUniqueIds,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(user)) return;
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return;

            string safeUser = SanitizeFileToken(user);
            string safeMachine = SanitizeFileToken(machine);
            string fileName = $"live_edits_{safeUser}_{safeMachine}.json";
            string clientId = BuildClientId(user, machine);

            var cleanIds = (elementUniqueIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return;

            try
            {
                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: cleanIds.Count > 0, ct);
                if (string.IsNullOrEmpty(cloudConnectorId)) return;

                string cacheKey = $"{projectName}::{clientId}";

                if (cleanIds.Count == 0)
                {
                    if (_liveEditsUploadFileIds.TryRemove(cacheKey, out string? cachedId) && !string.IsNullOrEmpty(cachedId))
                    {
                        try { await _api.DeleteOrTrashFileAsync(cachedId, cloudConnectorId, ct); } catch { }
                    }

                    // Also clean up any legacy filename live_edits_{safeUser}.json from v1.3.x
                    string legacyName = $"live_edits_{safeUser}.json";
                    var existing = await _api.ListFilesAsync(
                        $"'{cloudConnectorId}' in parents and (name = '{fileName.Replace("'", "\\'")}' or name = '{legacyName.Replace("'", "\\'")}') and trashed = false",
                        "id,name",
                        10,
                        ct);
                    foreach (var item in existing)
                    {
                        try { await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct); } catch { }
                    }
                    return;
                }

                var payload = new UserLiveEditsState
                {
                    ProjectId = projectName,
                    ClientId = clientId,
                    User = user,
                    Machine = machine,
                    UpdatedUtc = NetworkTimeHelper.GetAccurateUtc(),
                    ElementUniqueIds = cleanIds
                };
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

                if (_liveEditsUploadFileIds.TryGetValue(cacheKey, out string? knownId) && !string.IsNullOrEmpty(knownId))
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        await _api.UpdateFileContentAsync(knownId, ms, ct);
                        return;
                    }
                    catch
                    {
                        _liveEditsUploadFileIds.TryRemove(cacheKey, out _);
                    }
                }

                var matches = await _api.ListFilesAsync(
                    $"'{cloudConnectorId}' in parents and name = '{fileName.Replace("'", "\\'")}' and trashed = false",
                    "id,name,modifiedTime",
                    10,
                    ct);

                var primary = matches
                    .OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();

                if (primary != null)
                {
                    using var ms = new MemoryStream(bytes);
                    await _api.UpdateFileContentAsync(primary.Id, ms, ct);
                    _liveEditsUploadFileIds[cacheKey] = primary.Id;

                    foreach (var dup in matches.Where(x => x.Id != primary.Id))
                    {
                        try { await _api.DeleteOrTrashFileAsync(dup.Id, cloudConnectorId, ct); } catch { }
                    }
                }
                else
                {
                    using var ms = new MemoryStream(bytes);
                    var created = await _api.CreateAndUploadFileAsync(fileName, cloudConnectorId, ms, ct);
                    if (created != null && !string.IsNullOrEmpty(created.Id))
                    {
                        _liveEditsUploadFileIds[cacheKey] = created.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"PublishLiveEditsAsync warning su '{projectName}'", ex);
            }
        }

        public async Task<List<RemoteElementLockInfo>> GetRemoteLiveEditsAsync(
            string projectName,
            string currentUser,
            string currentMachine,
            CancellationToken ct = default)
        {
            var poll = await PollConnectorStateAsync(projectName, currentUser, currentMachine, ct);
            return poll.RemoteLiveLocks;
        }

        public async Task<string> SyncBorrowRequestsFileAsync(string projectName, CancellationToken ct = default)
        {
            string localProjectDir = GetLocalProjectDir(projectName);
            string connectorDir = Path.Combine(localProjectDir, ".connector");
            Directory.CreateDirectory(connectorDir);

            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return localProjectDir;

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return localProjectDir;

            try
            {
                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                if (!string.IsNullOrEmpty(cloudConnectorId))
                {
                    var items = await _api.ListFilesAsync(
                        $"'{cloudConnectorId}' in parents and name = 'borrow_requests.json' and trashed = false",
                        "id,name,md5Checksum,modifiedTime",
                        10,
                        ct);

                    var latest = items.OrderByDescending(f => f.ModifiedTime ?? DateTimeOffset.MinValue).FirstOrDefault();
                    if (latest != null)
                    {
                        _borrowFileIdCache[projectName] = latest.Id;
                        await SyncBorrowItemToLocalCacheAsync(projectName, localProjectDir, latest, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"Errore SyncBorrowRequestsFileAsync su '{projectName}'", ex);
                _folders.InvalidateSubfolder(cloudFolderId, ".connector");
            }

            return localProjectDir;
        }

        public async Task PushBorrowRequestsFileAsync(string projectName, string localProjectDir, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) return;

            string localBorrowFile = Path.Combine(localProjectDir, ".connector", "borrow_requests.json");
            if (!File.Exists(localBorrowFile)) return;

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return;

            string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: true, ct);
            if (string.IsNullOrEmpty(cloudConnectorId)) return;

            try
            {
                await UpsertSmallConnectorFileAsync(cloudConnectorId, "borrow_requests.json", localBorrowFile, _borrowFileIdCache, projectName, ct);
                _borrowRequestsMetaCache.TryRemove(projectName, out _);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"Errore PushBorrowRequestsFileAsync su '{projectName}'", ex);
            }
        }

        public async Task UpdateActiveUserSessionAsync(
            string projectName,
            string user,
            string machine,
            bool isActive,
            bool isSyncing = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return;
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return;

            try
            {
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId)) return;

                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: true, ct);
                if (string.IsNullOrEmpty(cloudConnectorId)) return;

                var sessions = await ReadActiveUsersFromConnectorAsync(cloudConnectorId, projectName, ct);
                var now = NetworkTimeHelper.GetAccurateUtc();
                sessions.RemoveAll(s => (now - s.LastHeartbeatUtc).TotalHours > 4 ||
                    (s.UserName.Equals(user, StringComparison.OrdinalIgnoreCase) && s.MachineName.Equals(machine, StringComparison.OrdinalIgnoreCase)));

                if (isActive && !string.IsNullOrWhiteSpace(user))
                {
                    sessions.Add(new UserSession
                    {
                        UserName = user,
                        MachineName = machine,
                        LastHeartbeatUtc = now,
                        IsSyncing = isSyncing
                    });
                }

                string localConnectorDir = Path.Combine(GetLocalProjectDir(projectName), ".connector");
                Directory.CreateDirectory(localConnectorDir);
                string localFile = Path.Combine(localConnectorDir, "active_users.json");
                await File.WriteAllTextAsync(localFile, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }), ct);

                await UpsertSmallConnectorFileAsync(cloudConnectorId, "active_users.json", localFile, _sessionsFileIdCache, projectName, ct);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"UpdateActiveUserSessionAsync warning su '{projectName}'", ex);
            }
        }

        public async Task<List<UserSession>> GetActiveUsersAsync(string projectName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return new List<UserSession>();
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return new List<UserSession>();

            try
            {
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId)) return new List<UserSession>();

                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudConnectorId)) return new List<UserSession>();

                return await ReadActiveUsersFromConnectorAsync(cloudConnectorId, projectName, ct);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CollaborationStateService", $"GetActiveUsersAsync warning su '{projectName}'", ex);
                return new List<UserSession>();
            }
        }

        private async Task<List<UserSession>> ReadActiveUsersFromConnectorAsync(string cloudConnectorId, string projectName, CancellationToken ct)
        {
            var items = await _api.ListFilesAsync(
                $"'{cloudConnectorId}' in parents and name = 'active_users.json' and trashed = false",
                "id,name,modifiedTime",
                10,
                ct);

            var latest = items.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).FirstOrDefault();
            if (latest == null) return new List<UserSession>();

            _sessionsFileIdCache[projectName] = latest.Id;
            using var ms = new MemoryStream();
            await _api.DownloadFileAsync(latest.Id, ms, ct);
            string json = Encoding.UTF8.GetString(ms.ToArray());
            var loaded = JsonSerializer.Deserialize<List<UserSession>>(json) ?? new List<UserSession>();
            var now = NetworkTimeHelper.GetAccurateUtc();
            return loaded.Where(s => (now - s.LastHeartbeatUtc).TotalHours <= 4).ToList();
        }

        private async Task UpsertSmallConnectorFileAsync(
            string cloudConnectorId,
            string fileName,
            string localFilePath,
            ConcurrentDictionary<string, string> idCache,
            string cacheKey,
            CancellationToken ct)
        {
            if (idCache.TryGetValue(cacheKey, out string? cachedId) && !string.IsNullOrEmpty(cachedId))
            {
                try
                {
                    using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await _api.UpdateFileContentAsync(cachedId, fs, ct);
                    return;
                }
                catch
                {
                    idCache.TryRemove(cacheKey, out _);
                }
            }

            var existing = await _api.ListFilesAsync(
                $"'{cloudConnectorId}' in parents and name = '{fileName.Replace("'", "\\'")}' and trashed = false",
                "id,name,modifiedTime",
                10,
                ct);

            var primary = existing.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).FirstOrDefault();
            if (primary != null)
            {
                using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await _api.UpdateFileContentAsync(primary.Id, fs, ct);
                idCache[cacheKey] = primary.Id;

                foreach (var dup in existing.Where(x => x.Id != primary.Id))
                {
                    try { await _api.DeleteOrTrashFileAsync(dup.Id, cloudConnectorId, ct); } catch { }
                }
            }
            else
            {
                using var fs = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var created = await _api.CreateAndUploadFileAsync(fileName, cloudConnectorId, fs, ct);
                if (created != null && !string.IsNullOrEmpty(created.Id))
                {
                    idCache[cacheKey] = created.Id;
                }
            }
        }

        internal static void MergeRemoteLiveEditStates(
            IEnumerable<UserLiveEditsState> states,
            string currentClientId,
            string currentUser,
            string currentMachine,
            DateTime nowUtc,
            double ttlMinutes,
            List<RemoteElementLockInfo> output)
        {
            var seenUniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in states.OrderBy(s => s.UpdatedUtc))
            {
                // Match by ClientId (user@machine) so two teammates with the same Revit username on different PCs are NEVER ignored!
                string stateClientId = !string.IsNullOrWhiteSpace(state.ClientId)
                    ? state.ClientId
                    : BuildClientId(state.User, state.Machine);

                if (string.Equals(stateClientId, currentClientId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Legacy fallback: if state had no machine name, check user+machine both matching
                if (string.IsNullOrWhiteSpace(state.Machine) &&
                    string.Equals(state.User, currentUser, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((nowUtc - state.UpdatedUtc).TotalMinutes > ttlMinutes) continue;
                if (state.ElementUniqueIds == null || state.ElementUniqueIds.Count == 0) continue;

                foreach (var uid in state.ElementUniqueIds)
                {
                    if (string.IsNullOrWhiteSpace(uid)) continue;
                    if (seenUniqueIds.Add(uid))
                    {
                        output.Add(new RemoteElementLockInfo
                        {
                            ElementUniqueId = uid,
                            OwnerClientId = stateClientId,
                            OwnerUser = state.User,
                            OwnerMachine = state.Machine,
                            UpdatedUtc = state.UpdatedUtc
                        });
                    }
                }
            }
        }
    }
}
