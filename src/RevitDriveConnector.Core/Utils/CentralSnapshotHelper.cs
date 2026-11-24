using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitDriveConnector.Core.Utils
{
    public class PendingUploadMarker
    {
        [JsonPropertyName("projectId")]
        public string ProjectId { get; set; } = string.Empty;

        [JsonPropertyName("centralPath")]
        public string CentralPath { get; set; } = string.Empty;

        [JsonPropertyName("user")]
        public string User { get; set; } = string.Empty;

        [JsonPropertyName("expectedBaseVersion")]
        public long? ExpectedBaseVersion { get; set; }

        [JsonPropertyName("expectedBaseHash")]
        public string? ExpectedBaseHash { get; set; }

        [JsonPropertyName("createdAtUtc")]
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }
    }

    /// <summary>
    /// Protects against "Local file incompatible with central model" corruption.
    /// Before Revit writes to the local Central cache during Sync, takes a lightweight NTFS hard-link snapshot
    /// and writes a pending_upload.json marker. If the cloud upload fails, prevents VerifyIntegrityAndAlign from
    /// blindly overwriting the user's modified Central cache and allows clean retry or safe recovery.
    /// </summary>
    public static class CentralSnapshotHelper
    {
        private const string PendingMarkerFileName = "pending_upload.json";
        private const string SnapshotDirName = "Central_PreSyncSnapshot";

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public static string GetPendingMarkerPath(string localProjectDir)
            => Path.Combine(localProjectDir, PendingMarkerFileName);

        public static string GetSnapshotDirPath(string localProjectDir)
            => Path.Combine(localProjectDir, SnapshotDirName);

        public static PendingUploadMarker? ReadPendingUpload(string localProjectDir)
        {
            try
            {
                string markerPath = GetPendingMarkerPath(localProjectDir);
                if (!File.Exists(markerPath)) return null;
                string json = File.ReadAllText(markerPath);
                return JsonSerializer.Deserialize<PendingUploadMarker>(json);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CentralSnapshotHelper", $"Impossibile leggere {PendingMarkerFileName}", ex);
                return null;
            }
        }

        public static void MarkPendingUpload(string localProjectDir, PendingUploadMarker marker)
        {
            try
            {
                Directory.CreateDirectory(localProjectDir);
                string markerPath = GetPendingMarkerPath(localProjectDir);
                string tempPath = markerPath + ".tmp." + Guid.NewGuid().ToString("N");
                string json = JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, markerPath, true);
                AppLogger.Info("CentralSnapshotHelper", $"Marker pending_upload salvato per '{marker.ProjectId}' (base v{marker.ExpectedBaseVersion}).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CentralSnapshotHelper", "Errore scrittura pending_upload.json", ex);
            }
        }

        public static void ClearPendingUpload(string localProjectDir)
        {
            try
            {
                string markerPath = GetPendingMarkerPath(localProjectDir);
                if (File.Exists(markerPath))
                {
                    File.Delete(markerPath);
                }

                string snapshotDir = GetSnapshotDirPath(localProjectDir);
                if (Directory.Exists(snapshotDir))
                {
                    try { Directory.Delete(snapshotDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CentralSnapshotHelper", "Errore pulizia marker pending_upload", ex);
            }
        }

        /// <summary>
        /// Creates an instant NTFS hard-link snapshot of the Central directory (falls back to copy if cross-volume).
        /// Because Revit creates/replaces files or writes new delta files, hard-linking unchanged backup files costs
        /// near-zero disk space and takes only a few milliseconds even for 1 GB projects.
        /// </summary>
        public static bool CreatePreSyncSnapshot(string localProjectDir)
        {
            try
            {
                string centralDir = Path.Combine(localProjectDir, "Central");
                if (!Directory.Exists(centralDir)) return false;

                string snapshotDir = GetSnapshotDirPath(localProjectDir);
                if (Directory.Exists(snapshotDir))
                {
                    try { Directory.Delete(snapshotDir, true); } catch { }
                }
                Directory.CreateDirectory(snapshotDir);

                bool isWindows = OperatingSystem.IsWindows();
                foreach (var srcFile in Directory.GetFiles(centralDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(centralDir, srcFile);
                    string fileName = Path.GetFileName(srcFile);
                    if (fileName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase) ||
                        rel.IndexOf("Revit_temp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    string dstFile = Path.Combine(snapshotDir, rel);
                    string? dstParent = Path.GetDirectoryName(dstFile);
                    if (!string.IsNullOrEmpty(dstParent)) Directory.CreateDirectory(dstParent);

                    // For the main .rvt file (which Revit may modify in-place via Win32 write), do a real copy only if small or hardlink when safe.
                    // Note: Revit's File-Based Worksharing writes a new file / updates header in-place on .rvt, so hardlink would mutate with .rvt!
                    // Therefore: hardlink immutable incremental backup files (.dat/.rws), copy small volatile files, and skip duplicating huge .rvt unless needed.
                    // Wait: if we want true rollback of .rvt in-place writes, only hardlink files that Revit doesn't overwrite in-place (like numbered backup .dat),
                    // or copy files <= 25 MB.
                    var fi = new FileInfo(srcFile);
                    bool isMutableInPlace =
                        fileName.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("incrementtable", StringComparison.OrdinalIgnoreCase) ||
                        fileName.StartsWith("editing", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("users.dat", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("worksetperms.dat", StringComparison.OrdinalIgnoreCase);

                    if (!isMutableInPlace && isWindows)
                    {
                        if (!CreateHardLink(dstFile, srcFile, IntPtr.Zero))
                        {
                            File.Copy(srcFile, dstFile, true);
                        }
                    }
                    else if (fi.Length <= 50 * 1024 * 1024)
                    {
                        File.Copy(srcFile, dstFile, true);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("CentralSnapshotHelper", "Creazione snapshot pre-sync non riuscita", ex);
                return false;
            }
        }
    }
}
