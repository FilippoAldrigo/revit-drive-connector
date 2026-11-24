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
    /// Serverless distributed synchronization lock manager backed by Google Drive API v3 (.connector/sync_lock_*.json).
    /// Supports deterministic multi-claim arbitration (for up to 10+ concurrent team members),
    /// direct O(1) fileId heartbeat updates (no redundant listing), and Shared Drive soft-trash compatibility.
    /// </summary>
    internal class CloudLockService
    {
        private readonly DriveApiClient _api;
        private readonly GoogleAuthService _auth;
        private readonly DriveFolderResolver _folders;
        private readonly Func<AppConfig> _configProvider;

        // Cache active lock token -> (DriveFileId, FileLockData) so Heartbeat & Release can target the exact file directly
        private readonly ConcurrentDictionary<string, (string FileId, FileLockData Data)> _activeLockFiles = new(StringComparer.OrdinalIgnoreCase);

        public CloudLockService(DriveApiClient api, GoogleAuthService auth, DriveFolderResolver folders, Func<AppConfig> configProvider)
        {
            _api = api;
            _auth = auth;
            _folders = folders;
            _configProvider = configProvider;
            _auth.OnAccountDisconnected += () => _activeLockFiles.Clear();
        }

        public static bool IsSyncLockFileName(string name)
        {
            return string.Equals(name, "sync_lock.json", StringComparison.OrdinalIgnoreCase) ||
                   (name.StartsWith("sync_lock_", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }

        internal async Task<List<(DriveFileItem driveItem, FileLockData data)>> ReadCloudLocksAsync(
            string cloudConnectorId,
            DateTime nowUtc,
            int defaultTimeoutSeconds,
            bool deleteExpired,
            CancellationToken ct)
        {
            var items = await _api.ListFilesAsync(
                $"'{cloudConnectorId}' in parents and trashed = false",
                "id,name,mimeType,createdTime,modifiedTime",
                100,
                ct);

            return await ReadCloudLocksFromItemsAsync(cloudConnectorId, items, nowUtc, defaultTimeoutSeconds, deleteExpired, ct);
        }

        internal async Task<List<(DriveFileItem driveItem, FileLockData data)>> ReadCloudLocksFromItemsAsync(
            string cloudConnectorId,
            IEnumerable<DriveFileItem> connectorItems,
            DateTime nowUtc,
            int defaultTimeoutSeconds,
            bool deleteExpired,
            CancellationToken ct)
        {
            var active = new List<(DriveFileItem driveItem, FileLockData data)>();
            var lockItems = connectorItems
                .Where(f => f.MimeType != "application/vnd.google-apps.folder" && IsSyncLockFileName(f.Name))
                .ToList();

            foreach (var item in lockItems)
            {
                try
                {
                    using var ms = new MemoryStream();
                    await _api.DownloadFileAsync(item.Id, ms, ct);
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    var data = JsonSerializer.Deserialize<FileLockData>(json);
                    if (data == null)
                    {
                        if (deleteExpired)
                        {
                            try { await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct); } catch { }
                        }
                        continue;
                    }

                    int tolerance = data.SkewToleranceSeconds > 0 ? data.SkewToleranceSeconds : FileLockData.DefaultSkewToleranceSeconds;
                    bool isExpired = data.ExpiresAtUtc.AddSeconds(tolerance) <= nowUtc;
                    bool isFutureAnomaly = data.AcquiredAtUtc > nowUtc.AddSeconds(60);

                    if (!isExpired && !isFutureAnomaly)
                    {
                        active.Add((item, data));
                    }
                    else if (deleteExpired)
                    {
                        try { await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("CloudLockService", $"Impossibile leggere file lock '{item.Name}' ({item.Id})", ex);
                }
            }

            return active;
        }

        public async Task<FileLockData?> GetActiveLockAsync(string projectName, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return null;

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId)) return null;

            string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudConnectorId)) return null;

            var locks = await ReadCloudLocksAsync(cloudConnectorId, NetworkTimeHelper.GetAccurateUtc(), _configProvider().SyncTimeoutSeconds, deleteExpired: true, ct);
            return SelectWinner(locks).data;
        }

        private static (DriveFileItem? driveItem, FileLockData? data) SelectWinner(List<(DriveFileItem driveItem, FileLockData data)> locks)
        {
            if (locks.Count == 0) return (null, null);
            var w = locks
                .OrderBy(x => string.Equals(x.driveItem.Name, "sync_lock.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(x => x.driveItem.CreatedTime ?? x.driveItem.ModifiedTime ?? DateTimeOffset.MaxValue)
                .ThenBy(x => x.driveItem.Id, StringComparer.Ordinal)
                .First();
            return (w.driveItem, w.data);
        }

        public async Task<LockResponse> AcquireProjectLockAsync(LockRequest lockReq, CancellationToken ct = default)
        {
            try
            {
                await _auth.EnsureConnectedAsync(allowInteractiveLogin: false, ct);
            }
            catch (Exception authEx)
            {
                return new LockResponse
                {
                    Status = SyncLockStatus.Error,
                    Message = authEx.Message
                };
            }

            try
            {
                int timeoutSeconds = lockReq.TimeoutSeconds > 0 ? lockReq.TimeoutSeconds : _configProvider().SyncTimeoutSeconds;
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(lockReq.ProjectId, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId))
                {
                    return new LockResponse
                    {
                        Status = SyncLockStatus.Error,
                        Message = $"Progetto '{lockReq.ProjectId}' non trovato su Google Drive."
                    };
                }

                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: true, ct);
                if (string.IsNullOrEmpty(cloudConnectorId))
                {
                    return new LockResponse
                    {
                        Status = SyncLockStatus.Error,
                        Message = "Impossibile accedere alla cartella '.connector' su Google Drive."
                    };
                }

                var now = NetworkTimeHelper.GetAccurateUtc();

                // 1. Check existing active locks
                var existingLocks = await ReadCloudLocksAsync(cloudConnectorId, now, timeoutSeconds, deleteExpired: true, ct);
                var otherHolder = existingLocks
                    .Select(x => x.data)
                    .FirstOrDefault(l => !(l.User.Equals(lockReq.User, StringComparison.OrdinalIgnoreCase) &&
                                           l.Machine.Equals(lockReq.Machine, StringComparison.OrdinalIgnoreCase)));

                if (otherHolder != null)
                {
                    int tolerance = otherHolder.SkewToleranceSeconds > 0 ? otherHolder.SkewToleranceSeconds : FileLockData.DefaultSkewToleranceSeconds;
                    int remainingSeconds = Math.Max(1, (int)(otherHolder.ExpiresAtUtc.AddSeconds(tolerance) - now).TotalSeconds);
                    return new LockResponse
                    {
                        Status = SyncLockStatus.LockDeniedBusy,
                        LockToken = null,
                        CurrentHolder = otherHolder.User,
                        CurrentHolderMachine = otherHolder.Machine,
                        AcquiredAtUtc = otherHolder.AcquiredAtUtc,
                        ExpiresAtUtc = otherHolder.ExpiresAtUtc,
                        Message = $"Il progetto è in fase di sincronizzazione su Google Drive da {otherHolder.User} ({otherHolder.Machine}). Riprova tra circa {remainingSeconds}s."
                    };
                }

                var ownExisting = existingLocks.FirstOrDefault(x =>
                    x.data.User.Equals(lockReq.User, StringComparison.OrdinalIgnoreCase) &&
                    x.data.Machine.Equals(lockReq.Machine, StringComparison.OrdinalIgnoreCase));

                if (ownExisting.data != null)
                {
                    ownExisting.data.ExpiresAtUtc = now.AddSeconds(timeoutSeconds);
                    ownExisting.data.DurationSeconds = timeoutSeconds;
                    byte[] extBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ownExisting.data, new JsonSerializerOptions { WriteIndented = true }));
                    using var extStream = new MemoryStream(extBytes);
                    await _api.UpdateFileContentAsync(ownExisting.driveItem.Id, extStream, ct);
                    _activeLockFiles[ownExisting.data.LockToken] = (ownExisting.driveItem.Id, ownExisting.data);

                    return new LockResponse
                    {
                        Status = SyncLockStatus.LockAcquired,
                        LockToken = ownExisting.data.LockToken,
                        CurrentHolder = ownExisting.data.User,
                        CurrentHolderMachine = ownExisting.data.Machine,
                        AcquiredAtUtc = ownExisting.data.AcquiredAtUtc,
                        ExpiresAtUtc = ownExisting.data.ExpiresAtUtc,
                        Message = "Lock Google Drive prolungato con successo."
                    };
                }

                // 2. Create unique claim file (POST) so concurrent teammates never overwrite each other
                string lockToken = Guid.NewGuid().ToString("N");
                var newLock = new FileLockData
                {
                    ProjectId = lockReq.ProjectId,
                    User = lockReq.User,
                    Machine = lockReq.Machine,
                    LockToken = lockToken,
                    AcquiredAtUtc = now,
                    ExpiresAtUtc = now.AddSeconds(timeoutSeconds),
                    DurationSeconds = timeoutSeconds,
                    SkewToleranceSeconds = FileLockData.DefaultSkewToleranceSeconds
                };

                string claimFileName = $"sync_lock_{lockToken}.json";
                byte[] claimBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newLock, new JsonSerializerOptions { WriteIndented = true }));
                DriveFileItem? createdClaim;
                using (var claimStream = new MemoryStream(claimBytes))
                {
                    createdClaim = await _api.CreateAndUploadFileAsync(claimFileName, cloudConnectorId, claimStream, ct);
                }

                // 3. Anti-collision arbitration delay + deterministic winner election
                await Task.Delay(800, ct);

                var postLocks = await ReadCloudLocksAsync(cloudConnectorId, NetworkTimeHelper.GetAccurateUtc(), timeoutSeconds, deleteExpired: false, ct);
                var winner = SelectWinner(postLocks);

                if (winner.data == null || !string.Equals(winner.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase))
                {
                    string? ownId = createdClaim?.Id ?? postLocks
                        .FirstOrDefault(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase))
                        .driveItem?.Id;

                    if (!string.IsNullOrEmpty(ownId))
                    {
                        try { await _api.DeleteOrTrashFileAsync(ownId, cloudConnectorId, ct); } catch { }
                    }

                    AppLogger.Info("CloudLockService", $"Lock conteso su '{lockReq.ProjectId}': vinto da {winner.data?.User} ({winner.data?.Machine}).");
                    return new LockResponse
                    {
                        Status = SyncLockStatus.LockDeniedBusy,
                        LockToken = null,
                        CurrentHolder = winner.data?.User ?? "un altro utente",
                        CurrentHolderMachine = winner.data?.Machine ?? "",
                        AcquiredAtUtc = winner.data?.AcquiredAtUtc,
                        ExpiresAtUtc = winner.data?.ExpiresAtUtc,
                        Message = $"Lock conteso: sincronizzazione già avviata da {winner.data?.User ?? "un collega"} ({winner.data?.Machine}). Riprova tra qualche secondo."
                    };
                }

                string winnerFileId = winner.driveItem?.Id ?? createdClaim?.Id ?? string.Empty;
                if (!string.IsNullOrEmpty(winnerFileId))
                {
                    _activeLockFiles[lockToken] = (winnerFileId, newLock);
                }

                AppLogger.Info("CloudLockService", $"Lock acquisito su '{lockReq.ProjectId}' da {lockReq.User} ({lockReq.Machine}), token={lockToken[..8]}.");
                return new LockResponse
                {
                    Status = SyncLockStatus.LockAcquired,
                    LockToken = newLock.LockToken,
                    CurrentHolder = newLock.User,
                    CurrentHolderMachine = newLock.Machine,
                    AcquiredAtUtc = newLock.AcquiredAtUtc,
                    ExpiresAtUtc = newLock.ExpiresAtUtc,
                    Message = "Lock Google Drive acquisito e verificato con successo."
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("CloudLockService", $"Errore acquisizione lock su '{lockReq.ProjectId}'", ex);
                return new LockResponse
                {
                    Status = SyncLockStatus.Error,
                    Message = $"Errore acquisizione lock su Google Drive: {ex.Message}"
                };
            }
        }

        public async Task<bool> HeartbeatProjectLockAsync(string projectName, string lockToken, string user, int extendSeconds, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(lockToken)) return false;
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return false;

            int ext = extendSeconds > 0 ? extendSeconds : _configProvider().SyncTimeoutSeconds;

            try
            {
                // Fast O(1) path using cached lock fileId (1 single API call instead of listing + downloading all locks)
                if (_activeLockFiles.TryGetValue(lockToken, out var cached) && !string.IsNullOrEmpty(cached.FileId))
                {
                    cached.Data.ExpiresAtUtc = NetworkTimeHelper.GetAccurateUtc().AddSeconds(ext);
                    cached.Data.DurationSeconds = ext;
                    byte[] fastBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cached.Data, new JsonSerializerOptions { WriteIndented = true }));
                    using var fastMs = new MemoryStream(fastBytes);
                    await _api.UpdateFileContentAsync(cached.FileId, fastMs, ct);
                    AppLogger.Debug("CloudLockService", $"Heartbeat O(1) riuscito per '{projectName}' (token={lockToken[..Math.Min(8, lockToken.Length)]}, +{ext}s).");
                    return true;
                }

                // Fallback lookup if cache wasn't populated
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId)) return false;

                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudConnectorId)) return false;

                var locks = await ReadCloudLocksAsync(cloudConnectorId, NetworkTimeHelper.GetAccurateUtc(), ext, deleteExpired: false, ct);
                var mine = locks.FirstOrDefault(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
                if (mine.driveItem == null || mine.data == null)
                {
                    return false;
                }

                mine.data.ExpiresAtUtc = NetworkTimeHelper.GetAccurateUtc().AddSeconds(ext);
                mine.data.DurationSeconds = ext;
                byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mine.data, new JsonSerializerOptions { WriteIndented = true }));
                using var ms = new MemoryStream(bytes);
                await _api.UpdateFileContentAsync(mine.driveItem.Id, ms, ct);
                _activeLockFiles[lockToken] = (mine.driveItem.Id, mine.data);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloudLockService", $"Heartbeat fallito per '{projectName}'", ex);
                _activeLockFiles.TryRemove(lockToken, out _);
                return false;
            }
        }

        public async Task<(bool isValid, string reason)> VerifyLockOwnershipAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(lockToken))
            {
                return (false, "Token di lock mancante. Caricamento bloccato per evitare sovrascritture.");
            }

            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth)
            {
                return (false, "Account Google non connesso o sessione scaduta.");
            }

            string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudFolderId))
            {
                return (false, $"Cartella progetto '{projectName}' non trovata su Google Drive.");
            }

            string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
            if (string.IsNullOrEmpty(cloudConnectorId))
            {
                return (false, "Il lock di sincronizzazione è stato rimosso o non è più presente su Google Drive.");
            }

            var now = NetworkTimeHelper.GetAccurateUtc();
            var activeLocks = await ReadCloudLocksAsync(cloudConnectorId, now, _configProvider().SyncTimeoutSeconds, deleteExpired: false, ct);

            var otherActive = activeLocks.FirstOrDefault(x =>
                !string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
            if (otherActive.data != null)
            {
                return (false, $"Il lock di sincronizzazione appartiene ora a '{otherActive.data.User}' ({otherActive.data.Machine}). Caricamento bloccato.");
            }

            bool stillMine = activeLocks.Any(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
            if (!stillMine)
            {
                return (false, "Il tuo lock di sincronizzazione è scaduto o è stato forzatamente sbloccato prima dell'upload. Riprova l'upload.");
            }

            return (true, string.Empty);
        }

        public async Task<bool> ReleaseProjectLockAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return false;

            try
            {
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                string? cloudConnectorId = !string.IsNullOrEmpty(cloudFolderId)
                    ? await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct)
                    : null;

                if (!string.IsNullOrWhiteSpace(lockToken) && _activeLockFiles.TryRemove(lockToken, out var cached) && !string.IsNullOrEmpty(cached.FileId))
                {
                    try
                    {
                        await _api.DeleteOrTrashFileAsync(cached.FileId, cloudConnectorId, ct);
                        AppLogger.Info("CloudLockService", $"Lock rilasciato O(1) per '{projectName}' (token={lockToken[..Math.Min(8, lockToken.Length)]}).");
                        return true;
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(cloudConnectorId)) return true;

                var items = await _api.ListFilesAsync(
                    $"'{cloudConnectorId}' in parents and trashed = false",
                    "id,name,mimeType,modifiedTime",
                    100,
                    ct);

                var now = NetworkTimeHelper.GetAccurateUtc();
                foreach (var item in items.Where(f => f.MimeType != "application/vnd.google-apps.folder" && IsSyncLockFileName(f.Name)))
                {
                    bool isExactTokenFile = !string.IsNullOrWhiteSpace(lockToken) &&
                                            string.Equals(item.Name, $"sync_lock_{lockToken}.json", StringComparison.OrdinalIgnoreCase);

                    if (isExactTokenFile)
                    {
                        try { await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct); } catch { }
                        continue;
                    }

                    try
                    {
                        using var ms = new MemoryStream();
                        await _api.DownloadFileAsync(item.Id, ms, ct);
                        var data = JsonSerializer.Deserialize<FileLockData>(Encoding.UTF8.GetString(ms.ToArray()));
                        int tolerance = data?.SkewToleranceSeconds > 0 ? data.SkewToleranceSeconds : FileLockData.DefaultSkewToleranceSeconds;
                        bool matchesToken = !string.IsNullOrWhiteSpace(lockToken) &&
                                            data != null &&
                                            string.Equals(data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase);
                        bool matchesUserNoToken = string.IsNullOrWhiteSpace(lockToken) &&
                                                  data != null &&
                                                  data.User.Equals(user, StringComparison.OrdinalIgnoreCase);
                        bool isExpired = data == null || data.ExpiresAtUtc.AddSeconds(tolerance) <= now;

                        if (matchesToken || matchesUserNoToken || isExpired)
                        {
                            await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct);
                        }
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CloudLockService", $"Errore rilascio lock su '{projectName}'", ex);
                return false;
            }
        }

        public async Task<bool> ForceBreakProjectLockAsync(string projectName, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) await _auth.InitializeAsync(ct);
            if (!_auth.HasAuth) return false;

            try
            {
                string? cloudFolderId = await _folders.GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId)) return false;

                string? cloudConnectorId = await _folders.GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudConnectorId)) return true;

                var items = await _api.ListFilesAsync(
                    $"'{cloudConnectorId}' in parents and trashed = false",
                    "id,name,mimeType",
                    100,
                    ct);

                foreach (var item in items.Where(f => f.MimeType != "application/vnd.google-apps.folder" && IsSyncLockFileName(f.Name)))
                {
                    await _api.DeleteOrTrashFileAsync(item.Id, cloudConnectorId, ct);
                }

                _activeLockFiles.Clear();
                AppLogger.Warn("CloudLockService", $"ForceBreakProjectLock eseguito su '{projectName}'.");
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("CloudLockService", $"Errore ForceBreakProjectLock su '{projectName}'", ex);
                return false;
            }
        }
    }
}
