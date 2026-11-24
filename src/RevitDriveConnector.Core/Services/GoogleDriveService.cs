using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services.Drive;

namespace RevitDriveConnector.Core.Services
{
    /// <summary>
    /// Modular facade over Google Drive API v3 services (100% OAuth2 serverless).
    /// Delegates to GoogleAuthService, DriveApiClient, DriveFolderResolver,
    /// CloudLockService, CollaborationStateService, and CloudProjectSyncService.
    /// </summary>
    public class GoogleDriveService
    {
        private static readonly HttpClient SharedHttp = new()
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        private readonly AppConfig? _fixedConfig;
        private readonly GoogleAuthService _auth;
        private readonly DriveApiClient _api;
        private readonly DriveFolderResolver _folders;
        private readonly CloudLockService _locks;
        private readonly CollaborationStateService _collab;
        private readonly CloudProjectSyncService _sync;

        public bool IsNativeGoogleDriveConnected => _auth.HasAuth;
        public string ConnectedEmail => _auth.ConnectedEmail;
        public string LocalCachePath => GetConfig().LocalCachePath;
        public string AppDataDir => _auth.AppDataDir;
        public string CredentialsPath => _auth.CredentialsPath;
        public string TokenStorePath => _auth.TokenStorePath;

        public GoogleDriveService()
        {
            _fixedConfig = null;
            _auth = new GoogleAuthService(SharedHttp, useFixedConfig: false);
            _api = new DriveApiClient(SharedHttp, _auth, () => GetConfig().UploadChunkSizeMb * 1024 * 1024);
            _folders = new DriveFolderResolver(_api, _auth, GetConfig);
            _locks = new CloudLockService(_api, _auth, _folders, GetConfig);
            _collab = new CollaborationStateService(_api, _auth, _folders, _locks, GetConfig);
            _sync = new CloudProjectSyncService(_api, _auth, _folders, _collab, GetConfig);
        }

        public GoogleDriveService(
            AppConfig customConfig,
            HttpMessageHandler? httpHandler = null,
            Func<CancellationToken, Task<string>>? customTokenProvider = null,
            string? customAppDataDir = null)
        {
            _fixedConfig = customConfig ?? throw new ArgumentNullException(nameof(customConfig));
            var http = httpHandler != null ? new HttpClient(httpHandler) { Timeout = TimeSpan.FromMinutes(15) } : SharedHttp;

            _auth = new GoogleAuthService(http, useFixedConfig: true, customTokenProvider: customTokenProvider, customAppDataDir: customAppDataDir);
            _api = new DriveApiClient(http, _auth, () => GetConfig().UploadChunkSizeMb * 1024 * 1024);
            _folders = new DriveFolderResolver(_api, _auth, GetConfig);
            _locks = new CloudLockService(_api, _auth, _folders, GetConfig);
            _collab = new CollaborationStateService(_api, _auth, _folders, _locks, GetConfig);
            _sync = new CloudProjectSyncService(_api, _auth, _folders, _collab, GetConfig);
        }

        private AppConfig GetConfig() => _fixedConfig ?? AppConfig.Load();

        public void ReloadConfig()
        {
            if (_fixedConfig == null)
            {
                AppConfig.InvalidateCache();
            }
        }

        // --- Authentication ---
        public Task<bool> InitializeAsync(CancellationToken ct = default) => _auth.InitializeAsync(ct);
        public Task<GoogleAccountInfo> GetAccountInfoAsync(CancellationToken ct = default) => _auth.GetAccountInfoAsync(ct);
        public Task<GoogleAccountInfo> ConnectAccountAsync(CancellationToken ct = default) => _auth.ConnectAccountAsync(ct);
        public GoogleAccountInfo DisconnectAccount() => _auth.DisconnectAccount();
        public Task<bool> SaveCredentialsAsync(string credentialsJson, CancellationToken ct = default) => _auth.SaveCredentialsAsync(credentialsJson, ct);

        // --- Projects & Sync ---
        public Task<List<CloudProjectItem>> ListProjectsAsync(CancellationToken ct = default) => _sync.ListProjectsAsync(ct);

        public Task<string> MountCloudProjectAsync(string projectName, string cloudFolderId, CancellationToken ct = default, Action<TransferProgressInfo>? progressCallback = null)
            => _sync.MountCloudProjectAsync(projectName, cloudFolderId, ct, progressCallback);

        public Task<string> DownloadAndPrepareProjectAsync(string projectName, string? preferredCloudFolderId = null, CancellationToken ct = default, Action<TransferProgressInfo>? progressCallback = null)
            => _sync.DownloadAndPrepareProjectAsync(projectName, preferredCloudFolderId, ct, progressCallback);

        public Task UploadProjectChangesAsync(
            string projectName,
            string localCentralFilePath,
            string user,
            long? expectedBaseVersion = null,
            string? expectedBaseHash = null,
            bool isInitialCreate = false,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
            => _sync.UploadProjectChangesAsync(projectName, localCentralFilePath, user, expectedBaseVersion, expectedBaseHash, isInitialCreate, ct, progressCallback);

        public Task UploadProjectLinksAsync(string projectName, string localLinksDir, CancellationToken ct = default)
            => _sync.UploadProjectLinksAsync(projectName, localLinksDir, ct);

        public Task CreateCloudProjectAsync(string projectName, string localCentralFilePath, string user, CancellationToken ct = default, Action<TransferProgressInfo>? progressCallback = null)
            => _sync.CreateCloudProjectAsync(projectName, localCentralFilePath, user, ct, progressCallback);

        public Task<SyncIntegrityResult> VerifyIntegrityAndAlignAsync(string projectName, string user, CancellationToken ct = default, Action<TransferProgressInfo>? progressCallback = null)
            => _sync.VerifyIntegrityAndAlignAsync(projectName, user, ct, progressCallback);

        // --- Distributed Locks ---
        public Task<FileLockData?> GetActiveLockAsync(string projectName, CancellationToken ct = default)
            => _locks.GetActiveLockAsync(projectName, ct);

        public Task<LockResponse> AcquireProjectLockAsync(LockRequest lockReq, CancellationToken ct = default)
            => _locks.AcquireProjectLockAsync(lockReq, ct);

        public Task<bool> HeartbeatProjectLockAsync(string projectName, string lockToken, string user, int extendSeconds, CancellationToken ct = default)
            => _locks.HeartbeatProjectLockAsync(projectName, lockToken, user, extendSeconds, ct);

        public Task<(bool isValid, string reason)> VerifyLockOwnershipAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
            => _locks.VerifyLockOwnershipAsync(projectName, lockToken, user, ct);

        public Task<bool> ReleaseProjectLockAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
            => _locks.ReleaseProjectLockAsync(projectName, lockToken, user, ct);

        public Task<bool> ForceBreakProjectLockAsync(string projectName, CancellationToken ct = default)
            => _locks.ForceBreakProjectLockAsync(projectName, ct);

        // --- Collaboration State (Unified Poller, Sessions, Borrow, Live Edits) ---
        public Task<ConnectorPollSnapshot> PollConnectorStateAsync(string projectName, string currentUser, string currentMachine, CancellationToken ct = default)
            => _collab.PollConnectorStateAsync(projectName, currentUser, currentMachine, ct);

        public Task PublishLiveEditsAsync(string projectName, string user, string machine, List<string> elementUniqueIds, CancellationToken ct = default)
            => _collab.PublishLiveEditsAsync(projectName, user, machine, elementUniqueIds, ct);

        public Task<List<RemoteElementLockInfo>> GetRemoteLiveEditsAsync(string projectName, string currentUser, string currentMachine, CancellationToken ct = default)
            => _collab.GetRemoteLiveEditsAsync(projectName, currentUser, currentMachine, ct);

        public Task UpdateActiveUserSessionAsync(string projectName, string user, string machine, bool isActive, bool isSyncing = false, CancellationToken ct = default)
            => _collab.UpdateActiveUserSessionAsync(projectName, user, machine, isActive, isSyncing, ct);

        public Task<List<UserSession>> GetActiveUsersAsync(string projectName, CancellationToken ct = default)
            => _collab.GetActiveUsersAsync(projectName, ct);

        public Task<string> SyncBorrowRequestsFileAsync(string projectName, CancellationToken ct = default)
            => _collab.SyncBorrowRequestsFileAsync(projectName, ct);

        public Task PushBorrowRequestsFileAsync(string projectName, string localProjectDir, CancellationToken ct = default)
            => _collab.PushBorrowRequestsFileAsync(projectName, localProjectDir, ct);

        public string GetLocalProjectDir(string projectName) => _collab.GetLocalProjectDir(projectName);

        public static string BuildClientId(string user, string machine)
            => CollaborationStateService.BuildClientId(user, machine);

        // --- Collaborators ---
        public Task<CollaboratorOperationResult> AddCollaboratorAsync(string projectName, string email, string role = "writer", bool sendNotification = true, CancellationToken ct = default)
            => _sync.AddCollaboratorAsync(projectName, email, role, sendNotification, ct);

        public Task<List<CollaboratorItem>> ListCollaboratorsAsync(string projectName, CancellationToken ct = default)
            => _sync.ListCollaboratorsAsync(projectName, ct);

        public Task<CollaboratorOperationResult> RemoveCollaboratorAsync(string projectName, string permissionId, CancellationToken ct = default)
            => _sync.RemoveCollaboratorAsync(projectName, permissionId, ct);
    }
}
