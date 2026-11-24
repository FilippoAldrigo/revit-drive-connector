using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using RevitDriveConnector.Core.Localization;

namespace RevitDriveConnector.Core.Models
{
    public enum SyncLockStatus
    {
        Unlocked,
        LockAcquired,
        LockDeniedBusy,
        LockExpired,
        Error
    }

    public class FileLockData
    {
        public const int DefaultSkewToleranceSeconds = 15;

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
        public int DurationSeconds { get; set; } = 120;

        [JsonPropertyName("skewToleranceSeconds")]
        public int SkewToleranceSeconds { get; set; } = DefaultSkewToleranceSeconds;
    }

    public class LockRequest
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 120;
    }

    public class LockResponse
    {
        [JsonPropertyName("status")]
        public SyncLockStatus Status { get; set; }

        [JsonPropertyName("lockToken")]
        public string? LockToken { get; set; }

        [JsonPropertyName("currentHolder")]
        public string? CurrentHolder { get; set; }

        [JsonPropertyName("currentHolderMachine")]
        public string? CurrentHolderMachine { get; set; }

        [JsonPropertyName("acquiredAtUtc")]
        public DateTime? AcquiredAtUtc { get; set; }

        [JsonPropertyName("expiresAtUtc")]
        public DateTime? ExpiresAtUtc { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class DaemonStatusInfo
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "Online";

        [JsonPropertyName("localCachePath")]
        public string LocalCachePath { get; set; } = string.Empty;

        [JsonPropertyName("googleDriveConnected")]
        public bool GoogleDriveConnected { get; set; }

        [JsonPropertyName("googleAccountEmail")]
        public string GoogleAccountEmail { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("clockOffsetMs")]
        public double ClockOffsetMs { get; set; }

        [JsonPropertyName("isTimeSynchronized")]
        public bool IsTimeSynchronized { get; set; }

        [JsonPropertyName("currentLockHolder")]
        public string? CurrentLockHolder { get; set; }

        [JsonPropertyName("currentLockMachine")]
        public string? CurrentLockMachine { get; set; }

        [JsonPropertyName("lockExpiresAtUtc")]
        public DateTime? LockExpiresAtUtc { get; set; }

        [JsonPropertyName("activeUsers")]
        public List<UserSession> ActiveUsers { get; set; } = new();

        [JsonPropertyName("timestampUtc")]
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    }

    public class CloudProjectItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("driveFolderId")]
        public string DriveFolderId { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public long Version { get; set; }

        [JsonPropertyName("lastModifiedUtc")]
        public DateTime LastModifiedUtc { get; set; }

        [JsonIgnore]
        public DateTime LastModifiedLocal => LastModifiedUtc.Kind == DateTimeKind.Utc
            ? LastModifiedUtc.ToLocalTime()
            : DateTime.SpecifyKind(LastModifiedUtc, DateTimeKind.Utc).ToLocalTime();

        [JsonPropertyName("lastModifiedBy")]
        public string LastModifiedBy { get; set; } = string.Empty;

        [JsonPropertyName("localCachePath")]
        public string? LocalCachePath { get; set; }

        [JsonPropertyName("centralRvtName")]
        public string CentralRvtName { get; set; } = string.Empty;

        [JsonPropertyName("isSharedWithMe")]
        public bool IsSharedWithMe { get; set; }

        [JsonPropertyName("sharedBy")]
        public string SharedBy { get; set; } = string.Empty;

        [JsonPropertyName("sourceType")]
        public string SourceType { get; set; } = "MyDrive";

        [JsonPropertyName("isMountedLocally")]
        public bool IsMountedLocally { get; set; } = true;

        [JsonPropertyName("cloudFolderId")]
        public string CloudFolderId { get; set; } = string.Empty;

        [JsonIgnore]
        public string SourceDisplay => SourceType == "SharedDrive"
            ? $"Shared Drive ({SharedBy})"
            : (IsSharedWithMe ? $"{Loc.Tr("Open_SharedBy")} {SharedBy}" : Loc.Tr("Open_MyDrive"));

        [JsonIgnore]
        public string StatusDisplay => IsMountedLocally ? Loc.Tr("Open_ReadyLocal") : Loc.Tr("Open_OnCloud");
    }

    public class GoogleAccountInfo
    {
        [JsonPropertyName("isConnected")]
        public bool IsConnected { get; set; }

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("pictureUrl")]
        public string PictureUrl { get; set; } = string.Empty;

        [JsonPropertyName("hasClientCredentials")]
        public bool HasClientCredentials { get; set; }

        [JsonPropertyName("statusMessage")]
        public string StatusMessage { get; set; } = string.Empty;
    }

    public class SyncIntegrityResult
    {
        [JsonPropertyName("isAligned")]
        public bool IsAligned { get; set; }

        [JsonPropertyName("isWaitingForDownload")]
        public bool IsWaitingForDownload { get; set; }

        [JsonPropertyName("wasUpdatedFromCloud")]
        public bool WasUpdatedFromCloud { get; set; }

        [JsonPropertyName("hasPendingLocalUpload")]
        public bool HasPendingLocalUpload { get; set; }

        [JsonPropertyName("pendingUploadCanRetryDirectly")]
        public bool PendingUploadCanRetryDirectly { get; set; }

        [JsonPropertyName("lastSyncUser")]
        public string LastSyncUser { get; set; } = string.Empty;

        [JsonPropertyName("cloudVersion")]
        public long CloudVersion { get; set; }

        [JsonPropertyName("expectedHash")]
        public string ExpectedHash { get; set; } = string.Empty;

        [JsonPropertyName("currentLocalHash")]
        public string CurrentLocalHash { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CollaboratorItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = "writer";

        [JsonPropertyName("roleDisplay")]
        public string RoleDisplay => Role switch
        {
            "owner" => Loc.Tr("Common_RoleOwner"),
            "organizer" => Loc.Tr("Common_RoleOwner"),
            "fileOrganizer" => Loc.Tr("Common_RoleEditor"),
            "writer" => Loc.Tr("Common_RoleEditor"),
            "reader" => Loc.Tr("Common_RoleViewer"),
            _ => Role
        };

        [JsonPropertyName("photoLink")]
        public string? PhotoLink { get; set; }
    }

    public class AddCollaboratorRequest
    {
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = "writer";

        [JsonPropertyName("sendNotificationEmail")]
        public bool SendNotificationEmail { get; set; } = true;
    }

    public class RemoveCollaboratorRequest
    {
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = string.Empty;

        [JsonPropertyName("permissionId")]
        public string PermissionId { get; set; } = string.Empty;
    }

    public class CollaboratorOperationResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("collaborator")]
        public CollaboratorItem? Collaborator { get; set; }
    }

    public class UserLiveEditsState
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("updatedUtc")]
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("elementUniqueIds")]
        public List<string> ElementUniqueIds { get; set; } = new();
    }

    public class RemoteElementLockInfo
    {
        [JsonPropertyName("elementUniqueId")]
        public string ElementUniqueId { get; set; } = string.Empty;

        [JsonPropertyName("ownerClientId")]
        public string OwnerClientId { get; set; } = string.Empty;

        [JsonPropertyName("ownerUser")]
        public string OwnerUser { get; set; } = string.Empty;

        [JsonPropertyName("ownerMachine")]
        public string OwnerMachine { get; set; } = string.Empty;

        [JsonPropertyName("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }
    }

    public class TransferProgressInfo
    {
        public string Phase { get; set; } = string.Empty;
        public string CurrentFileName { get; set; } = string.Empty;
        public int CompletedFiles { get; set; }
        public int TotalFiles { get; set; }
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }

        public double Percentage => TotalBytes > 0
            ? Math.Min(100.0, Math.Max(0.0, (double)BytesTransferred * 100.0 / TotalBytes))
            : (TotalFiles > 0 ? Math.Min(100.0, (double)CompletedFiles * 100.0 / TotalFiles) : 0.0);
    }

    public class ConnectorPollSnapshot
    {
        [JsonPropertyName("remoteLiveLocks")]
        public List<RemoteElementLockInfo> RemoteLiveLocks { get; set; } = new();

        [JsonPropertyName("pendingBorrowRequests")]
        public List<BorrowRequest> PendingBorrowRequests { get; set; } = new();

        [JsonPropertyName("outgoingBorrowRequests")]
        public List<BorrowRequest> OutgoingBorrowRequests { get; set; } = new();

        [JsonPropertyName("activeSyncLock")]
        public FileLockData? ActiveSyncLock { get; set; }
    }

    public class PreflightCheckRequest
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("minRequiredFreeBytes")]
        public long MinRequiredFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024; // 2 GB default
    }

    public class PreflightCheckResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("isAuthValid")]
        public bool IsAuthValid { get; set; }

        [JsonPropertyName("authEmail")]
        public string AuthEmail { get; set; } = string.Empty;

        [JsonPropertyName("isDiskSpaceSufficient")]
        public bool IsDiskSpaceSufficient { get; set; }

        [JsonPropertyName("availableFreeBytes")]
        public long AvailableFreeBytes { get; set; }

        [JsonPropertyName("requiredFreeBytes")]
        public long RequiredFreeBytes { get; set; }

        [JsonPropertyName("driveLetter")]
        public string DriveLetter { get; set; } = string.Empty;

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    public class ProjectRevisionInfo
    {
        [JsonPropertyName("revisionId")]
        public string RevisionId { get; set; } = string.Empty;

        [JsonPropertyName("modifiedTimeUtc")]
        public DateTime ModifiedTimeUtc { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }

        [JsonPropertyName("authorName")]
        public string AuthorName { get; set; } = string.Empty;

        [JsonPropertyName("authorEmail")]
        public string AuthorEmail { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public long Version { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class ProjectHistoryResponse
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("currentVersion")]
        public long CurrentVersion { get; set; }

        [JsonPropertyName("revisions")]
        public List<ProjectRevisionInfo> Revisions { get; set; } = new();
    }

    public class RollbackRequest
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("revisionId")]
        public string RevisionId { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    public class RollbackResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("restoredVersion")]
        public long RestoredVersion { get; set; }

        [JsonPropertyName("newVersion")]
        public long NewVersion { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class CloudLinkUpdateItem
    {
        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("cloudFileId")]
        public string CloudFileId { get; set; } = string.Empty;

        [JsonPropertyName("cloudSizeBytes")]
        public long CloudSizeBytes { get; set; }

        [JsonPropertyName("cloudModifiedTimeUtc")]
        public DateTime CloudModifiedTimeUtc { get; set; }

        [JsonPropertyName("localExists")]
        public bool LocalExists { get; set; }

        [JsonPropertyName("localSizeBytes")]
        public long LocalSizeBytes { get; set; }

        [JsonPropertyName("localModifiedTimeUtc")]
        public DateTime LocalModifiedTimeUtc { get; set; }

        [JsonPropertyName("hasNewCloudVersion")]
        public bool HasNewCloudVersion { get; set; }
    }

    public class CheckCloudLinksResponse
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("updatesAvailable")]
        public bool UpdatesAvailable { get; set; }

        [JsonPropertyName("links")]
        public List<CloudLinkUpdateItem> Links { get; set; } = new();
    }

    public class DiskCleanupResult
    {
        [JsonPropertyName("freedBytes")]
        public long FreedBytes { get; set; }

        [JsonPropertyName("deletedFilesCount")]
        public int DeletedFilesCount { get; set; }

        [JsonPropertyName("deletedDirectoriesCount")]
        public int DeletedDirectoriesCount { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public class DiagnosticsExportResponse
    {
        [JsonPropertyName("zipPath")]
        public string ZipPath { get; set; } = string.Empty;

        [JsonPropertyName("zipSizeBytes")]
        public long ZipSizeBytes { get; set; }

        [JsonPropertyName("fileCount")]
        public int FileCount { get; set; }
    }
}
