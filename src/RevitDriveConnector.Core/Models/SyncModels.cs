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

    public class LockRequest
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("machine")]
        public string Machine { get; set; } = string.Empty;

        [JsonPropertyName("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 60;
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

        [JsonPropertyName("coordinatorUrl")]
        public string CoordinatorUrl { get; set; } = string.Empty;

        [JsonPropertyName("cloudStoragePath")]
        public string CloudStoragePath { get; set; } = string.Empty;

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
        public string SourceDisplay => IsSharedWithMe ? $"{Loc.Tr("Open_SharedBy")} {SharedBy}" : Loc.Tr("Open_MyDrive");

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
}
