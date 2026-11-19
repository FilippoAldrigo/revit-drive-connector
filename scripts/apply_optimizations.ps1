# Script di applicazione patch e ottimizzazioni
$root = "C:\Users\phili\Desktop\googledriveconector"

# 1. Patch Program.cs in Daemon
$progPath = Join-Path $root "src\RevitDriveConnector.Daemon\Program.cs"
$progText = [System.IO.File]::ReadAllText($progPath)

$progText = $progText.Replace(
    'string cloudProjectDir = Path.Combine(DriveService.CloudStoragePath, lockReq.ProjectId);',
    'string cloudProjectDir = DriveShortcutResolver.ResolveProjectDirectory(DriveService.CloudStoragePath, lockReq.ProjectId);'
)
$progText = $progText.Replace(
    'string upCloudProjectDir = Path.Combine(DriveService.CloudStoragePath, upProj);',
    'string upCloudProjectDir = DriveShortcutResolver.ResolveProjectDirectory(DriveService.CloudStoragePath, upProj);'
)
$progText = $progText.Replace(
    'string pDir = Path.Combine(DriveService.CloudStoragePath, bReq.ProjectId);',
    'string pDir = DriveShortcutResolver.ResolveProjectDirectory(DriveService.CloudStoragePath, bReq.ProjectId);'
)
[System.IO.File]::WriteAllText($progPath, $progText, [System.Text.Encoding]::UTF8)
Write-Host "Daemon Program.cs patchato." -ForegroundColor Green

# 2. Patch GoogleDriveService.cs
$gdsPath = Join-Path $root "src\RevitDriveConnector.Daemon\Services\GoogleDriveService.cs"
$gdsText = [System.IO.File]::ReadAllText($gdsPath)

# 2a. MountCloudProjectAsync
$gdsText = $gdsText.Replace(
    'string remoteProjectDir = Path.Combine(_config.CloudStoragePath, projectName);',
    'string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);'
)

# 2b. DownloadCloudFolderRecursivelyAsync - skip Local
$oldDl = 'foreach (var item in res.Files)
			{
				string localPath = Path.Combine(localDir, item.Name);'
if ($gdsText.Contains($oldDl)) {
    $newDl = 'foreach (var item in res.Files)
			{
				if (string.Equals(item.Name, "Local", StringComparison.OrdinalIgnoreCase)) continue;
				string localPath = Path.Combine(localDir, item.Name);'
    $gdsText = $gdsText.Replace($oldDl, $newDl)
} else {
    # Prova con CRLF generico
    $regexDl = 'foreach\s*\(\s*var\s+item\s+in\s+res\.Files\s*\)\s*\{\s*string\s+localPath\s*=\s*Path\.Combine\(localDir,\s*item\.Name\);'
    $gdsText = [System.Text.RegularExpressions.Regex]::Replace($gdsText, $regexDl, "foreach (var item in res.Files)`r`n			{`r`n				if (string.Equals(item.Name, `"Local`", StringComparison.OrdinalIgnoreCase)) continue;`r`n				string localPath = Path.Combine(localDir, item.Name);")
}

# 2c. UploadCloudFolderRecursivelyAsync - skip Local
$regexUp = 'foreach\s*\(\s*var\s+subDir\s+in\s+Directory\.GetDirectories\(localDir\)\s*\)\s*\{\s*string\s+subName\s*=\s*Path\.GetFileName\(subDir\);'
$gdsText = [System.Text.RegularExpressions.Regex]::Replace($gdsText, $regexUp, "foreach (var subDir in Directory.GetDirectories(localDir))`r`n			{`r`n				string subName = Path.GetFileName(subDir);`r`n				if (string.Equals(subName, `"Local`", StringComparison.OrdinalIgnoreCase)) continue;")

# 2d. UploadProjectChangesAsync - isolamento upload API ed evitare duplicati fisici su G:
$oldLocalSync = '			try
			{
				if (Directory.Exists(_config.CloudStoragePath))
				{
					Directory.CreateDirectory(remoteCentralDir);
					await SyncDirectoryAsync(localCentralDir, remoteCentralDir, ct);

					string localLinksDir = Path.Combine(localProjectDir, "Links");
					string remoteLinksDir = Path.Combine(remoteProjectDir, "Links");
					if (Directory.Exists(localLinksDir))
					{
						await SyncDirectoryAsync(localLinksDir, remoteLinksDir, ct);
					}

					string remoteManifestFile = Path.Combine(remoteProjectDir, "manifest.json");
					await File.WriteAllTextAsync(remoteManifestFile, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
				}
			}
			catch { }'

$newLocalSync = '			// Sincronizza su disco G: solo se non si usa l''API diretta Google Drive
			if (_driveService == null || string.IsNullOrEmpty(cloudFolderId))
			{
				try
				{
					if (Directory.Exists(_config.CloudStoragePath) && Directory.Exists(remoteProjectDir))
					{
						Directory.CreateDirectory(remoteCentralDir);
						await SyncDirectoryAsync(localCentralDir, remoteCentralDir, ct);

						string localLinksDir = Path.Combine(localProjectDir, "Links");
						string remoteLinksDir = Path.Combine(remoteProjectDir, "Links");
						if (Directory.Exists(localLinksDir))
						{
							await SyncDirectoryAsync(localLinksDir, remoteLinksDir, ct);
						}

						string remoteManifestFile = Path.Combine(remoteProjectDir, "manifest.json");
						await File.WriteAllTextAsync(remoteManifestFile, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
					}
				}
				catch { }
			}'
$gdsText = $gdsText.Replace($oldLocalSync, $newLocalSync)

# 2e. GetProjectFolderIdAsync - ricerca cartelle E scorciatoie
$oldSearch = '				var search = _driveService.Files.List();
				search.Q = $"name = ''{projectName}'' and mimeType = ''application/vnd.google-apps.folder'' and trashed = false";
				search.Fields = "files(id, name, parents, shared, owners)";
				search.SupportsAllDrives = true;
				search.IncludeItemsFromAllDrives = true;
				search.PageSize = 10;
				var res = await search.ExecuteAsync(ct);

				if (res?.Files != null && res.Files.Count > 0)
				{
					var target = res.Files.FirstOrDefault(f => f.Shared == true) ?? res.Files[0];
					return target.Id;
				}'

$newSearch = '				var search = _driveService.Files.List();
				search.Q = $"name = ''{projectName}'' and (mimeType = ''application/vnd.google-apps.folder'' or mimeType = ''application/vnd.google-apps.shortcut'') and trashed = false";
				search.Fields = "files(id, name, mimeType, shortcutDetails, parents, shared, owners)";
				search.SupportsAllDrives = true;
				search.IncludeItemsFromAllDrives = true;
				search.PageSize = 10;
				var res = await search.ExecuteAsync(ct);

				if (res?.Files != null && res.Files.Count > 0)
				{
					foreach (var f in res.Files)
					{
						if (f.MimeType == "application/vnd.google-apps.shortcut" && f.ShortcutDetails?.TargetId != null)
						{
							return f.ShortcutDetails.TargetId;
						}
					}
					var target = res.Files.FirstOrDefault(f => f.Shared == true) ?? res.Files[0];
					return target.Id;
				}'
$gdsText = $gdsText.Replace($oldSearch, $newSearch)

[System.IO.File]::WriteAllText($gdsPath, $gdsText, [System.Text.Encoding]::UTF8)
Write-Host "GoogleDriveService.cs patchato." -ForegroundColor Green

# 3. Patch SynchronizationCoordinator.cs
$syncCoordPath = Join-Path $root "src\RevitDriveConnector.Addin\Services\SynchronizationCoordinator.cs"
$syncText = [System.IO.File]::ReadAllText($syncCoordPath)

$syncText = $syncText.Replace(
    'ProjectId = ActiveProjectId ?? Path.GetFileNameWithoutExtension(centralPath),',
    'ProjectId = ExtractProjectName(centralPath),'
)

if (-not $syncText.Contains('public static string ExtractProjectName')) {
    $extractMethod = '		public static string ExtractProjectName(string centralPath)
		{
			if (!string.IsNullOrEmpty(ActiveProjectId))
			{
				string p = ActiveProjectId;
				if (p.EndsWith("_Central", StringComparison.OrdinalIgnoreCase))
					p = p.Substring(0, p.Length - "_Central".Length);
				return p;
			}

			try
			{
				var centralDir = Path.GetDirectoryName(centralPath);
				if (centralDir != null)
				{
					var projectDir = Path.GetDirectoryName(centralDir);
					if (projectDir != null)
					{
						string pName = Path.GetFileName(projectDir);
						if (!string.IsNullOrEmpty(pName) && !pName.Equals("RevitDriveCache", StringComparison.OrdinalIgnoreCase))
						{
							return pName;
						}
					}
				}
			}
			catch { }

			string name = Path.GetFileNameWithoutExtension(centralPath);
			if (name.EndsWith("_Central", StringComparison.OrdinalIgnoreCase))
			{
				name = name.Substring(0, name.Length - "_Central".Length);
			}
			return name;
		}

		public static string GetCentralModelPath'

    $syncText = $syncText.Replace('public static string GetCentralModelPath', $extractMethod)
}
[System.IO.File]::WriteAllText($syncCoordPath, $syncText, [System.Text.Encoding]::UTF8)
Write-Host "SynchronizationCoordinator.cs patchato." -ForegroundColor Green

# 4. Patch BorrowNotificationService.cs
$bnsPath = Join-Path $root "src\RevitDriveConnector.Addin\Services\BorrowNotificationService.cs"
$bnsText = [System.IO.File]::ReadAllText($bnsPath)
$bnsText = $bnsText.Replace(
    'string projectId = SynchronizationCoordinator.ActiveProjectId ?? Path.GetFileNameWithoutExtension(centralPath);',
    'string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);'
)
[System.IO.File]::WriteAllText($bnsPath, $bnsText, [System.Text.Encoding]::UTF8)
Write-Host "BorrowNotificationService.cs patchato." -ForegroundColor Green
