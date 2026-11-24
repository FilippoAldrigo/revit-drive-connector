using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services.Drive
{
    /// <summary>
    /// Resolves and caches Google Drive folder IDs for projects and standard subfolders (.connector, Central, Links),
    /// supporting My Drive, Shared With Me, Drive shortcuts, and Google Workspace Shared Drives.
    /// </summary>
    internal class DriveFolderResolver
    {
        private readonly DriveApiClient _api;
        private readonly GoogleAuthService _auth;
        private readonly Func<AppConfig> _configProvider;

        private readonly ConcurrentDictionary<string, string> _projectFolderIdCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _subfolderIdCache = new(StringComparer.OrdinalIgnoreCase);

        public DriveFolderResolver(DriveApiClient api, GoogleAuthService auth, Func<AppConfig> configProvider)
        {
            _api = api;
            _auth = auth;
            _configProvider = configProvider;
            _auth.OnAccountDisconnected += ClearCaches;
        }

        public void ClearCaches()
        {
            _projectFolderIdCache.Clear();
            _subfolderIdCache.Clear();
        }

        public void CacheProjectFolderId(string projectName, string folderId)
        {
            if (!string.IsNullOrWhiteSpace(projectName) && !string.IsNullOrWhiteSpace(folderId))
            {
                _projectFolderIdCache[projectName] = folderId;
            }
        }

        public void InvalidateSubfolder(string parentFolderId, string subFolderName)
        {
            _subfolderIdCache.TryRemove($"{parentFolderId}::{subFolderName}", out _);
        }

        public async Task<string?> GetSubfolderIdAsync(string parentFolderId, string subFolderName, bool createIfMissing, CancellationToken ct)
        {
            if (!_auth.HasAuth || string.IsNullOrWhiteSpace(parentFolderId)) return null;

            string cacheKey = $"{parentFolderId}::{subFolderName}";
            if (_subfolderIdCache.TryGetValue(cacheKey, out string? cachedSubId) && !string.IsNullOrEmpty(cachedSubId))
            {
                return cachedSubId;
            }

            string query = $"'{parentFolderId}' in parents and name = '{subFolderName.Replace("'", "\\'")}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            var files = await _api.ListFilesAsync(query, "id,name,createdTime,modifiedTime", 20, ct);

            if (files.Count > 0)
            {
                string canonicalId = files
                    .OrderBy(f => f.CreatedTime ?? f.ModifiedTime ?? DateTimeOffset.MaxValue)
                    .ThenBy(f => f.Id, StringComparer.Ordinal)
                    .First()
                    .Id;
                _subfolderIdCache[cacheKey] = canonicalId;
                return canonicalId;
            }

            if (!createIfMissing) return null;

            var created = await _api.CreateMetadataOnlyAsync(new
            {
                name = subFolderName,
                mimeType = "application/vnd.google-apps.folder",
                parents = new[] { parentFolderId }
            }, ct);

            // Deterministic deduplication if two teammates create the subfolder concurrently
            await Task.Delay(250, ct);
            var postFiles = await _api.ListFilesAsync(query, "id,name,createdTime,modifiedTime", 20, ct);
            if (postFiles.Count > 1)
            {
                var canonical = postFiles
                    .OrderBy(f => f.CreatedTime ?? f.ModifiedTime ?? DateTimeOffset.MaxValue)
                    .ThenBy(f => f.Id, StringComparer.Ordinal)
                    .First();

                if (!string.Equals(canonical.Id, created.Id, StringComparison.Ordinal))
                {
                    try { await _api.DeleteOrTrashFileAsync(created.Id, parentFolderId, ct); } catch { }
                    _subfolderIdCache[cacheKey] = canonical.Id;
                    return canonical.Id;
                }
            }

            _subfolderIdCache[cacheKey] = created.Id;
            return created.Id;
        }

        public async Task<string?> GetProjectFolderIdAsync(string projectName, bool createIfMissing = true, CancellationToken ct = default)
        {
            if (!_auth.HasAuth || string.IsNullOrWhiteSpace(projectName)) return null;

            var cfg = _configProvider();
            string localManifest = Path.Combine(cfg.LocalCachePath, projectName, "manifest.json");
            if (File.Exists(localManifest))
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(localManifest, ct);
                    var m = JsonSerializer.Deserialize<ProjectManifest>(mJson);
                    if (!string.IsNullOrEmpty(m?.CloudFolderId))
                    {
                        _projectFolderIdCache[projectName] = m.CloudFolderId;
                        return m.CloudFolderId;
                    }
                }
                catch { }
            }

            if (_projectFolderIdCache.TryGetValue(projectName, out string? cachedProjId) && !string.IsNullOrEmpty(cachedProjId))
            {
                return cachedProjId;
            }

            try
            {
                string escapedName = projectName.Replace("'", "\\'");
                var files = await _api.ListFilesAsync(
                    $"name = '{escapedName}' and (mimeType = 'application/vnd.google-apps.folder' or mimeType = 'application/vnd.google-apps.shortcut') and trashed = false",
                    "id,name,mimeType,shortcutDetails,parents,shared,driveId,owners",
                    20,
                    ct);

                if (files.Count > 0)
                {
                    foreach (var f in files)
                    {
                        if (f.MimeType == "application/vnd.google-apps.shortcut" && !string.IsNullOrEmpty(f.ShortcutDetails?.TargetId))
                        {
                            _projectFolderIdCache[projectName] = f.ShortcutDetails.TargetId;
                            return f.ShortcutDetails.TargetId;
                        }
                    }

                    // Prefer Shared Drive or shared folders over unshared personal duplicates
                    var target = files.FirstOrDefault(f => !string.IsNullOrEmpty(f.DriveId))
                                 ?? files.FirstOrDefault(f => f.Shared == true)
                                 ?? files[0];
                    _projectFolderIdCache[projectName] = target.Id;
                    return target.Id;
                }

                string? ensuredId = createIfMissing ? await EnsureCloudProjectFolderExistsAsync(projectName, ct) : null;
                if (!string.IsNullOrEmpty(ensuredId))
                {
                    _projectFolderIdCache[projectName] = ensuredId;
                }
                return ensuredId;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DriveFolderResolver", $"Errore ricerca cartella progetto '{projectName}'", ex);
                return null;
            }
        }

        public async Task<string?> EnsureCloudProjectFolderExistsAsync(string projectName, CancellationToken ct = default)
        {
            if (!_auth.HasAuth) return null;

            try
            {
                string parentId = "root";
                var parentFiles = await _api.ListFilesAsync(
                    "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                    "id,name,driveId,shared",
                    10,
                    ct);

                if (parentFiles.Count > 0)
                {
                    parentId = (parentFiles.FirstOrDefault(f => !string.IsNullOrEmpty(f.DriveId)) ?? parentFiles[0]).Id;
                }
                else
                {
                    var createdParent = await _api.CreateMetadataOnlyAsync(new
                    {
                        name = "RevitProjects",
                        mimeType = "application/vnd.google-apps.folder"
                    }, ct);
                    parentId = createdParent.Id;
                }

                var createdFolder = await _api.CreateMetadataOnlyAsync(new
                {
                    name = projectName,
                    mimeType = "application/vnd.google-apps.folder",
                    parents = new[] { parentId }
                }, ct);
                return createdFolder.Id;
            }
            catch (Exception ex)
            {
                AppLogger.Error("DriveFolderResolver", $"Errore creazione cartella cloud '{projectName}'", ex);
                return null;
            }
        }
    }
}
