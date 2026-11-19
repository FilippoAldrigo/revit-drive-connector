using System;
using System.IO;

namespace RevitDriveConnector.Core.Utils
{
    public static class DriveShortcutResolver
    {
        public static string ResolveProjectDirectory(string cloudStoragePath, string projectName, string? cloudFolderId = null)
        {
            if (string.IsNullOrEmpty(cloudStoragePath) || string.IsNullOrEmpty(projectName))
                return Path.Combine(cloudStoragePath ?? "", projectName ?? "");

            string directPath = Path.Combine(cloudStoragePath, projectName);
            if (Directory.Exists(directPath))
            {
                return directPath;
            }

            // 1. Controlla se esiste una scorciatoia Windows (.lnk) creata da Google Drive Desktop
            string lnkPath = Path.Combine(cloudStoragePath, $"{projectName}.lnk");
            if (File.Exists(lnkPath))
            {
                string? target = ResolveShortcut(lnkPath);
                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                {
                    return target;
                }
            }

            // 2. Controlla percorso standard .shortcut-targets-by-id se abbiamo cloudFolderId
            if (!string.IsNullOrEmpty(cloudFolderId))
            {
                try
                {
                    string driveRoot = Path.GetPathRoot(cloudStoragePath) ?? "G:\\";
                    string shortcutTarget = Path.Combine(driveRoot, ".shortcut-targets-by-id", cloudFolderId, projectName);
                    if (Directory.Exists(shortcutTarget))
                    {
                        return shortcutTarget;
                    }

                    string directTarget = Path.Combine(driveRoot, ".shortcut-targets-by-id", cloudFolderId);
                    if (Directory.Exists(directTarget))
                    {
                        return directTarget;
                    }
                }
                catch { }
            }

            return directPath;
        }

        public static string? ResolveShortcut(string lnkPath)
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(lnkPath)) return null;

            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic? shell = Activator.CreateInstance(shellType);
                    dynamic? sc = shell?.CreateShortcut(lnkPath);
                    string target = sc?.TargetPath ?? "";
                    if (!string.IsNullOrEmpty(target))
                    {
                        return target;
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
