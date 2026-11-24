using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.Models
{
 /// <summary>
 /// Metadati completi di un progetto Revit condiviso su Google Drive
 /// </summary>
 public class ProjectManifest
 {
 [JsonPropertyName("projectId")]
 public string ProjectId { get; set; } = Guid.NewGuid().ToString();

 [JsonPropertyName("projectName")]
 public string ProjectName { get; set; } = string.Empty;

 [JsonPropertyName("cloudFolderId")]
 public string? CloudFolderId { get; set; }

 [JsonPropertyName("revitVersion")]
 public string RevitVersion { get; set; } = "2027";

 [JsonPropertyName("version")]
 public long Version { get; set; } = 1;

 [JsonPropertyName("lastSyncUtc")]
 public DateTime LastSyncUtc { get; set; } = DateTime.UtcNow;

 [JsonPropertyName("lastSyncUser")]
 public string LastSyncUser { get; set; } = string.Empty;

 [JsonPropertyName("lastSyncMachine")]
 public string LastSyncMachine { get; set; } = string.Empty;

 [JsonPropertyName("centralRvtName")]
 public string CentralRvtName { get; set; } = string.Empty;

 [JsonPropertyName("centralFileSha256")]
 public string CentralFileSha256 { get; set; } = string.Empty;

 [JsonPropertyName("centralFileSizeBytes")]
 public long CentralFileSizeBytes { get; set; }

 [JsonPropertyName("files")]
 public List<FileEntry> Files { get; set; } = new();

 [JsonPropertyName("activeUsers")]
 public List<UserSession> ActiveUsers { get; set; } = new();
 }

 /// <summary>
 /// Singolo file tracciato nel progetto (es. Progetto_Central.rvt o file di backup .slog/.rws)
 /// </summary>
 public class FileEntry
 {
 [JsonPropertyName("relativePath")]
 public string RelativePath { get; set; } = string.Empty;

 [JsonPropertyName("sha256")]
 public string Sha256 { get; set; } = string.Empty;

 [JsonPropertyName("sizeBytes")]
 public long SizeBytes { get; set; }

 [JsonPropertyName("lastModifiedUtc")]
 public DateTime LastModifiedUtc { get; set; }

 [JsonPropertyName("driveFileId")]
 public string? DriveFileId { get; set; }
 }

 /// <summary>
 /// Informazioni sulla sessione utente attiva
 /// </summary>
 public class UserSession
 {
 [JsonPropertyName("userName")]
 public string UserName { get; set; } = string.Empty;

 [JsonPropertyName("machineName")]
 public string MachineName { get; set; } = string.Empty;

 [JsonPropertyName("lastHeartbeatUtc")]
 public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

 [JsonPropertyName("isSyncing")]
 public bool IsSyncing { get; set; }
 }
}
