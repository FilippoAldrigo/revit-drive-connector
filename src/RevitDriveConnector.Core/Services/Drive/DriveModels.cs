using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.Services.Drive
{
    internal class DriveFileListResponse
    {
        [JsonPropertyName("nextPageToken")]
        public string? NextPageToken { get; set; }

        [JsonPropertyName("files")]
        public List<DriveFileItem>? Files { get; set; }
    }

    internal class DriveFileItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? Size { get; set; }

        [JsonPropertyName("md5Checksum")]
        public string? Md5Checksum { get; set; }

        [JsonPropertyName("createdTime")]
        public DateTimeOffset? CreatedTime { get; set; }

        [JsonPropertyName("modifiedTime")]
        public DateTimeOffset? ModifiedTime { get; set; }

        [JsonPropertyName("shared")]
        public bool? Shared { get; set; }

        [JsonPropertyName("driveId")]
        public string? DriveId { get; set; }

        [JsonPropertyName("parents")]
        public List<string>? Parents { get; set; }

        [JsonPropertyName("owners")]
        public List<DriveOwnerItem>? Owners { get; set; }

        [JsonPropertyName("lastModifyingUser")]
        public DriveOwnerItem? LastModifyingUser { get; set; }

        [JsonPropertyName("shortcutDetails")]
        public DriveShortcutDetails? ShortcutDetails { get; set; }
    }

    internal class DriveOwnerItem
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("emailAddress")]
        public string? EmailAddress { get; set; }

        [JsonPropertyName("photoLink")]
        public string? PhotoLink { get; set; }

        [JsonPropertyName("me")]
        public bool? Me { get; set; }
    }

    internal class DriveShortcutDetails
    {
        [JsonPropertyName("targetId")]
        public string? TargetId { get; set; }
    }

    internal class DriveAboutResponse
    {
        [JsonPropertyName("user")]
        public DriveOwnerItem? User { get; set; }
    }

    internal class DrivePermissionListResponse
    {
        [JsonPropertyName("permissions")]
        public List<DrivePermissionItem>? Permissions { get; set; }
    }

    internal class DrivePermissionItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("emailAddress")]
        public string? EmailAddress { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("photoLink")]
        public string? PhotoLink { get; set; }
    }

    internal class DriveRevisionListResponse
    {
        [JsonPropertyName("revisions")]
        public List<DriveRevisionItem>? Revisions { get; set; }
    }

    internal class DriveRevisionItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }

        [JsonPropertyName("modifiedTime")]
        public DateTimeOffset? ModifiedTime { get; set; }

        [JsonPropertyName("size")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? Size { get; set; }

        [JsonPropertyName("md5Checksum")]
        public string? Md5Checksum { get; set; }

        [JsonPropertyName("keepForever")]
        public bool? KeepForever { get; set; }

        [JsonPropertyName("lastModifyingUser")]
        public DriveOwnerItem? LastModifyingUser { get; set; }
    }
}
