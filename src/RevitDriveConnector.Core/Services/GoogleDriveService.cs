using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services
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

        [JsonPropertyName("parents")]
        public List<string>? Parents { get; set; }

        [JsonPropertyName("owners")]
        public List<DriveOwnerItem>? Owners { get; set; }

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

    public class GoogleDriveService
    {
        private static readonly HttpClient SharedHttp = new()
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        private static readonly string[] Scopes = new[]
        {
            "https://www.googleapis.com/auth/drive",
            "https://www.googleapis.com/auth/drive.file"
        };

        private UserCredential? _credential;
        private readonly AppConfig _config;
        private readonly bool _useFixedConfig;
        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task<string>>? _customTokenProvider;
        private string _connectedEmail = string.Empty;

        private bool HasAuth => _credential != null || _customTokenProvider != null;
        public bool IsNativeGoogleDriveConnected => HasAuth;
        public string ConnectedEmail => _connectedEmail;
        public string CloudStoragePath => _config.CloudStoragePath;
        public string LocalCachePath => _config.LocalCachePath;

        public string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector");
        public string CredentialsPath => Path.Combine(AppDataDir, "credentials.json");
        public string TokenStorePath => Path.Combine(AppDataDir, "token_store");

        public GoogleDriveService()
        {
            _config = AppConfig.Load();
            _useFixedConfig = false;
            _http = SharedHttp;
            Directory.CreateDirectory(AppDataDir);
        }

        public GoogleDriveService(AppConfig customConfig, HttpMessageHandler? httpHandler = null, Func<CancellationToken, Task<string>>? customTokenProvider = null)
        {
            _config = customConfig ?? throw new ArgumentNullException(nameof(customConfig));
            _useFixedConfig = true;
            _http = httpHandler != null ? new HttpClient(httpHandler) { Timeout = TimeSpan.FromMinutes(15) } : SharedHttp;
            _customTokenProvider = customTokenProvider;
        }

        public void ReloadConfig()
        {
            if (_useFixedConfig) return;

            var fresh = AppConfig.Load();
            _config.CoordinatorUrl = fresh.CoordinatorUrl;
            _config.CloudStoragePath = fresh.CloudStoragePath;
            _config.LocalCachePath = fresh.LocalCachePath;
            _config.Language = fresh.Language;
            _config.SyncTimeoutSeconds = fresh.SyncTimeoutSeconds;
        }

        private void EnsureCredentialsExist()
        {
            if (File.Exists(CredentialsPath)) return;

            var candidatePaths = new List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "credentials.json"),
                Path.Combine(AppDataDir, "bin", "credentials.json")
            };

            string? dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                candidatePaths.Add(Path.Combine(dir, "credentials.json"));
                candidatePaths.Add(Path.Combine(dir, "dist", "payload", "bin", "credentials.json"));
                dir = Path.GetDirectoryName(dir);
            }

            foreach (var candidate in candidatePaths)
            {
                if (File.Exists(candidate))
                {
                    try
                    {
                        File.Copy(candidate, CredentialsPath, true);
                        break;
                    }
                    catch { }
                }
            }
        }

        private void InvalidateCredentialAndClearTokenStore()
        {
            _credential = null;
            _connectedEmail = string.Empty;
            try
            {
                if (Directory.Exists(TokenStorePath))
                {
                    Directory.Delete(TokenStorePath, true);
                }
            }
            catch { }
        }

        private static bool IsAuthenticationError(Exception ex)
        {
            if (ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException tre)
            {
                string err = tre.Error?.Error ?? "";
                return string.Equals(err, "invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(err, "invalid_client", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(err, "unauthorized_client", StringComparison.OrdinalIgnoreCase) ||
                       tre.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
            }
            if (ex is HttpRequestException httpEx && httpEx.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return true;
            }
            return ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(HttpMethod method, string url, CancellationToken ct)
        {
            if (!HasAuth)
            {
                throw new InvalidOperationException("Account Google non autenticato.");
            }

            string token;
            try
            {
                token = _customTokenProvider != null
                    ? await _customTokenProvider(ct)
                    : await _credential!.GetAccessTokenForRequestAsync(null, ct);
            }
            catch (Exception ex) when (IsAuthenticationError(ex))
            {
                Console.WriteLine($"[GoogleDriveService] Token OAuth scaduto o revocato: {ex.Message}. Pulizia token_store...");
                InvalidateCredentialAndClearTokenStore();
                throw;
            }

            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        private async Task<GoogleAccountInfo?> ValidateCurrentCredentialAsync(CancellationToken ct)
        {
            if (!HasAuth) return null;

            try
            {
                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user", ct);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    Console.WriteLine("[GoogleDriveService] Risposta 401 Unauthorized da Google Drive API. Pulizia token_store...");
                    InvalidateCredentialAndClearTokenStore();
                    return null;
                }

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    var about = JsonSerializer.Deserialize<DriveAboutResponse>(json);
                    if (about?.User != null)
                    {
                        _connectedEmail = about.User.EmailAddress ?? "";
                        return new GoogleAccountInfo
                        {
                            IsConnected = true,
                            HasClientCredentials = File.Exists(CredentialsPath),
                            Email = _connectedEmail,
                            DisplayName = about.User.DisplayName ?? "",
                            PictureUrl = about.User.PhotoLink ?? "",
                            StatusMessage = $"Connesso come {about.User.DisplayName} ({_connectedEmail})"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Verifica stato utente: {ex.Message}");
                if (IsAuthenticationError(ex))
                {
                    InvalidateCredentialAndClearTokenStore();
                }
            }

            return null;
        }

        public async Task<bool> InitializeAsync(CancellationToken ct = default)
        {
            if (_customTokenProvider != null) return true;
            if (_useFixedConfig) return false;

            try
            {
                EnsureCredentialsExist();
                if (File.Exists(CredentialsPath) && Directory.Exists(TokenStorePath) && Directory.GetFiles(TokenStorePath).Length > 0)
                {
                    using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read);
                    _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        GoogleClientSecrets.FromStream(stream).Secrets,
                        Scopes,
                        "user",
                        ct,
                        new FileDataStore(TokenStorePath, true));

                    var validated = await ValidateCurrentCredentialAsync(ct);
                    if (validated == null || !validated.IsConnected)
                    {
                        _credential = null;
                        _connectedEmail = string.Empty;
                        Console.WriteLine("[GoogleDriveService] Sessione Google salvata non valida o scaduta. Necessario nuovo accesso.");
                        return false;
                    }

                    Console.WriteLine($"[GoogleDriveService] Connessione automatica Google Drive API v3 riuscita ({_connectedEmail}).");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[GoogleDriveService] Cloud Storage attivo in: {_config.CloudStoragePath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Inizializzazione storage: {ex.Message}");
                if (IsAuthenticationError(ex))
                {
                    InvalidateCredentialAndClearTokenStore();
                }
                return false;
            }
        }

        public async Task<GoogleAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
        {
            EnsureCredentialsExist();
            if (!HasAuth && Directory.Exists(TokenStorePath) && Directory.GetFiles(TokenStorePath).Length > 0)
            {
                await InitializeAsync(ct);
            }

            if (HasAuth)
            {
                var validated = await ValidateCurrentCredentialAsync(ct);
                if (validated != null && validated.IsConnected)
                {
                    return validated;
                }
            }

            bool hasCreds = File.Exists(CredentialsPath);
            return new GoogleAccountInfo
            {
                HasClientCredentials = hasCreds,
                IsConnected = false,
                StatusMessage = hasCreds
                    ? "Pronto per l'accesso Google. Clicca 'Accedi con Google' per collegare il tuo account."
                    : "Credenziali Google (credentials.json) non configurate."
            };
        }

        public async Task<GoogleAccountInfo> ConnectAccountAsync(CancellationToken ct = default)
        {
            EnsureCredentialsExist();

            if (!File.Exists(CredentialsPath))
            {
                return new GoogleAccountInfo
                {
                    IsConnected = false,
                    HasClientCredentials = false,
                    StatusMessage = "File credentials.json mancante. Configura prima il Client ID Google."
                };
            }

            try
            {
                using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read);
                var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

                _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    secrets,
                    Scopes,
                    "user",
                    ct,
                    new FileDataStore(TokenStorePath, true));

                var info = await GetAccountInfoAsync(ct);
                if (!info.IsConnected)
                {
                    throw new Exception("Account non convalidato post-autenticazione.");
                }
                return info;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Errore connessione OAuth: {ex.Message}. Tento ri-autenticazione pulita del browser...");
                try
                {
                    if (Directory.Exists(TokenStorePath))
                    {
                        Directory.Delete(TokenStorePath, true);
                    }

                    using var stream = new FileStream(CredentialsPath, FileMode.Open, FileAccess.Read);
                    var secrets = GoogleClientSecrets.FromStream(stream).Secrets;

                    _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                        secrets,
                        Scopes,
                        "user",
                        ct,
                        new FileDataStore(TokenStorePath, true));

                    return await GetAccountInfoAsync(ct);
                }
                catch (Exception retryEx)
                {
                    Console.WriteLine($"[GoogleDriveService] Ri-autenticazione fallita: {retryEx.Message}");
                    return new GoogleAccountInfo
                    {
                        IsConnected = false,
                        HasClientCredentials = true,
                        StatusMessage = $"Errore di autenticazione: {retryEx.Message}"
                    };
                }
            }
        }

        public GoogleAccountInfo DisconnectAccount()
        {
            _credential = null;
            _connectedEmail = string.Empty;
            try
            {
                if (Directory.Exists(TokenStorePath))
                {
                    Directory.Delete(TokenStorePath, true);
                }
            }
            catch { }

            return new GoogleAccountInfo
            {
                IsConnected = false,
                HasClientCredentials = File.Exists(CredentialsPath),
                StatusMessage = "Account Google disconnesso con successo."
            };
        }

        public async Task<bool> SaveCredentialsAsync(string credentialsJson, CancellationToken ct = default)
        {
            try
            {
                await File.WriteAllTextAsync(CredentialsPath, credentialsJson, ct);
                DisconnectAccount();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Errore salvataggio credenziali: {ex.Message}");
                return false;
            }
        }

        private async Task<List<DriveFileItem>> DriveListFilesAsync(string query, string fields, int pageSize = 100, CancellationToken ct = default)
        {
            var allFiles = new List<DriveFileItem>();
            string? pageToken = null;
            bool fetchAllPages = pageSize >= 100;
            int maxPages = fetchAllPages ? 20 : 1;

            for (int page = 0; page < maxPages; page++)
            {
                string url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString($"nextPageToken,files({fields})")}&pageSize={pageSize}&supportsAllDrives=true&includeItemsFromAllDrives=true";
                if (!string.IsNullOrEmpty(pageToken))
                {
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                }

                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, url, ct);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                var parsed = JsonSerializer.Deserialize<DriveFileListResponse>(json);
                if (parsed?.Files != null)
                {
                    allFiles.AddRange(parsed.Files);
                }

                pageToken = parsed?.NextPageToken;
                if (!fetchAllPages || string.IsNullOrEmpty(pageToken))
                {
                    break;
                }
            }

            return allFiles;
        }

        private async Task<DriveFileItem?> DriveGetFileMetadataAsync(string fileId, string fields, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(fields)}&supportsAllDrives=true";
            using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, url, ct);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DriveFileItem>(json);
        }

        private async Task DriveDownloadFileAsync(string fileId, Stream destination, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true";
            using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, url, ct);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            using var contentStream = await resp.Content.ReadAsStreamAsync(ct);
            await contentStream.CopyToAsync(destination, 81920, ct);
        }

        private async Task<DriveFileItem> DriveCreateMetadataOnlyAsync(object bodyObj, CancellationToken ct = default)
        {
            string url = "https://www.googleapis.com/drive/v3/files?supportsAllDrives=true&fields=id,name,mimeType,createdTime";
            using var req = await CreateAuthorizedRequestAsync(HttpMethod.Post, url, ct);
            req.Content = new StringContent(JsonSerializer.Serialize(bodyObj), Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DriveFileItem>(json) ?? new DriveFileItem();
        }

        private async Task<DriveFileItem?> DriveCreateAndUploadFileAsync(string fileName, string parentFolderId, Stream fileStream, CancellationToken ct = default)
        {
            // Initiate resumable upload session
            string initUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true&fields=id,name,createdTime,modifiedTime";
            using var initReq = await CreateAuthorizedRequestAsync(HttpMethod.Post, initUrl, ct);
            initReq.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            initReq.Headers.Add("X-Upload-Content-Length", fileStream.Length.ToString());

            var metaObj = new
            {
                name = fileName,
                parents = new[] { parentFolderId }
            };
            initReq.Content = new StringContent(JsonSerializer.Serialize(metaObj), Encoding.UTF8, "application/json");

            using var initResp = await _http.SendAsync(initReq, ct);
            initResp.EnsureSuccessStatusCode();

            Uri? uploadUri = initResp.Headers.Location;
            if (uploadUri == null)
            {
                throw new InvalidOperationException("Google Drive non ha restituito l'URL di upload resumable.");
            }

            using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUri);
            var streamContent = new StreamContent(fileStream, 262144);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            putReq.Content = streamContent;

            using var putResp = await _http.SendAsync(putReq, ct);
            putResp.EnsureSuccessStatusCode();
            try
            {
                string respJson = await putResp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(respJson))
                {
                    return JsonSerializer.Deserialize<DriveFileItem>(respJson);
                }
            }
            catch { }
            return null;
        }

        private async Task DriveUpdateFileContentAsync(string fileId, Stream fileStream, CancellationToken ct = default)
        {
            string initUrl = $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=resumable&supportsAllDrives=true";
            using var initReq = await CreateAuthorizedRequestAsync(HttpMethod.Patch, initUrl, ct);
            initReq.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            initReq.Headers.Add("X-Upload-Content-Length", fileStream.Length.ToString());
            initReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var initResp = await _http.SendAsync(initReq, ct);
            initResp.EnsureSuccessStatusCode();

            Uri? uploadUri = initResp.Headers.Location;
            if (uploadUri == null)
            {
                throw new InvalidOperationException("Google Drive non ha restituito l'URL di aggiornamento resumable.");
            }

            using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUri);
            var streamContent = new StreamContent(fileStream, 262144);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            putReq.Content = streamContent;

            using var putResp = await _http.SendAsync(putReq, ct);
            putResp.EnsureSuccessStatusCode();
        }

        private async Task DriveDeleteFileAsync(string fileId, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true";
            using var req = await CreateAuthorizedRequestAsync(HttpMethod.Delete, url, ct);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
        }

        private bool IsLocalFallbackStorageOnly()
        {
            return string.IsNullOrWhiteSpace(_config.CloudStoragePath) ||
                   _config.CloudStoragePath.IndexOf("RevitDrive_CloudStorage", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public async Task EnsureGoogleConnectedIfNeededAsync(CancellationToken ct = default)
        {
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            // Se l'utente non ha ancora eseguito l'accesso a Google Drive API e non ha un disco Google Drive Desktop (G:\),
            // avvia automaticamente l'autenticazione OAuth 2.0 nel browser
            if (!HasAuth && IsLocalFallbackStorageOnly())
            {
                var acct = await ConnectAccountAsync(ct);
                if (!acct.IsConnected)
                {
                    throw new InvalidOperationException(
                        $"Per caricare il modello su Google Drive è necessario collegare il tuo Account Google.\nDettaglio: {acct.StatusMessage}");
                }
            }
        }

        private async Task ScanLocalProjectsDirectoryAsync(
            List<CloudProjectItem> list,
            string rootPath,
            bool useShortcutResolver,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return;

            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                string pName = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(pName) ||
                    pName.StartsWith(".") ||
                    pName.StartsWith("[") ||
                    string.Equals(pName, "Transition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string resolvedDir = useShortcutResolver
                    ? DriveShortcutResolver.ResolveProjectDirectory(rootPath, pName)
                    : dir;
                string centralDir = Path.Combine(resolvedDir, "Central");
                if (!Directory.Exists(centralDir)) continue;

                string? centralFile = FindCentralRvtFile(centralDir, pName);
                if (string.IsNullOrEmpty(centralFile)) continue;
                var fileInfo = new FileInfo(centralFile);

                string manifestFile = Path.Combine(resolvedDir, "manifest.json");
                ProjectManifest? manifest = null;
                if (File.Exists(manifestFile))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(manifestFile, ct);
                        manifest = JsonSerializer.Deserialize<ProjectManifest>(json);
                    }
                    catch { }
                }

                var existing = list.FirstOrDefault(p =>
                    string.Equals(p.Name, pName, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(manifest?.CloudFolderId) &&
                     string.Equals(p.CloudFolderId, manifest.CloudFolderId, StringComparison.OrdinalIgnoreCase)));

                if (existing != null)
                {
                    existing.IsMountedLocally = true;
                    if (string.IsNullOrEmpty(existing.CentralRvtName))
                    {
                        existing.CentralRvtName = Path.GetFileName(centralFile);
                    }
                    if (string.IsNullOrEmpty(existing.CloudFolderId) && !string.IsNullOrEmpty(manifest?.CloudFolderId))
                    {
                        existing.CloudFolderId = manifest.CloudFolderId;
                    }
                    if (manifest != null && manifest.Version > existing.Version)
                    {
                        existing.Version = manifest.Version;
                        existing.LastModifiedUtc = manifest.LastSyncUtc;
                        existing.LastModifiedBy = manifest.LastSyncUser;
                    }
                    continue;
                }

                list.Add(new CloudProjectItem
                {
                    Id = manifest?.ProjectId ?? pName,
                    Name = pName,
                    DriveFolderId = resolvedDir,
                    CloudFolderId = manifest?.CloudFolderId ?? "",
                    Version = manifest?.Version ?? 1,
                    LastModifiedUtc = manifest?.LastSyncUtc ?? fileInfo.LastWriteTimeUtc,
                    LastModifiedBy = manifest?.LastSyncUser ?? "Autodesk User",
                    CentralRvtName = Path.GetFileName(centralFile),
                    IsMountedLocally = true,
                    IsSharedWithMe = false,
                    SourceType = "MyDrive",
                    SharedBy = ""
                });
            }
        }

        private void MergeOrAddCloudProject(
            List<CloudProjectItem> list,
            DriveFileItem projectFolder,
            ProjectManifest? cloudManifest,
            bool forceSharedWithMe = false)
        {
            string projectName = projectFolder.Name;
            string folderId = projectFolder.Id;
            bool isSharedWithMe = forceSharedWithMe;
            string sharedBy = "";

            if (projectFolder.Owners != null && projectFolder.Owners.Count > 0)
            {
                var owner = projectFolder.Owners[0];
                if (owner.Me != true)
                {
                    isSharedWithMe = true;
                    sharedBy = string.IsNullOrEmpty(owner.DisplayName)
                        ? (owner.EmailAddress ?? "")
                        : $"{owner.DisplayName} ({owner.EmailAddress})";
                }
            }

            long version = cloudManifest?.Version ?? 1;
            DateTime modifiedUtc = cloudManifest?.LastSyncUtc ?? projectFolder.ModifiedTime?.UtcDateTime ?? DateTime.UtcNow;
            string modifiedBy = !string.IsNullOrEmpty(cloudManifest?.LastSyncUser)
                ? cloudManifest.LastSyncUser
                : (!string.IsNullOrEmpty(sharedBy) ? sharedBy : "Google Drive User");

            var existing = list.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(p.CloudFolderId) && string.Equals(p.CloudFolderId, folderId, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.CloudFolderId = folderId;
                if (cloudManifest != null && cloudManifest.Version >= existing.Version)
                {
                    existing.Version = cloudManifest.Version;
                    existing.LastModifiedUtc = cloudManifest.LastSyncUtc;
                    if (!string.IsNullOrEmpty(cloudManifest.LastSyncUser))
                    {
                        existing.LastModifiedBy = cloudManifest.LastSyncUser;
                    }
                }
                if (isSharedWithMe)
                {
                    existing.IsSharedWithMe = true;
                    if (!string.IsNullOrEmpty(sharedBy)) existing.SharedBy = sharedBy;
                    existing.SourceType = "SharedWithMe";
                }
            }
            else
            {
                list.Add(new CloudProjectItem
                {
                    Id = cloudManifest?.ProjectId ?? projectName,
                    Name = projectName,
                    DriveFolderId = folderId,
                    CloudFolderId = folderId,
                    Version = version,
                    LastModifiedUtc = modifiedUtc,
                    LastModifiedBy = modifiedBy,
                    IsSharedWithMe = isSharedWithMe,
                    SharedBy = sharedBy,
                    SourceType = isSharedWithMe ? "SharedWithMe" : "MyDrive",
                    IsMountedLocally = false
                });
            }
        }

        private async Task<ProjectManifest?> TryDownloadManifestAsync(string manifestFileId, CancellationToken ct)
        {
            try
            {
                using var ms = new MemoryStream();
                await DriveDownloadFileAsync(manifestFileId, ms, ct);
                string json = Encoding.UTF8.GetString(ms.ToArray());
                return JsonSerializer.Deserialize<ProjectManifest>(json);
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<CloudProjectItem>> ListProjectsAsync(CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            var list = new List<CloudProjectItem>();

            // 1. Scansione locale: Google Drive Desktop (CloudStoragePath) + Cache locale (LocalCachePath)
            await ScanLocalProjectsDirectoryAsync(list, _config.CloudStoragePath, useShortcutResolver: true, ct);
            await ScanLocalProjectsDirectoryAsync(list, _config.LocalCachePath, useShortcutResolver: false, ct);

            // 2. Se l'Account Google è connesso, scansiona il Cloud via Google Drive API v3
            if (HasAuth)
            {
                var seenFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    // 2a. Ricerca tramite manifest.json accessibili
                    var manifestFiles = await DriveListFilesAsync(
                        "name = 'manifest.json' and trashed = false",
                        "id,name,parents,owners,shared,modifiedTime",
                        200,
                        ct);

                    foreach (var mf in manifestFiles)
                    {
                        if (mf.Parents == null || mf.Parents.Count == 0) continue;
                        string parentFolderId = mf.Parents[0];
                        if (!seenFolderIds.Add(parentFolderId)) continue;

                        try
                        {
                            var projectFolder = await DriveGetFileMetadataAsync(parentFolderId, "id,name,owners,shared,modifiedTime", ct);
                            if (projectFolder == null) continue;

                            var cloudManifest = await TryDownloadManifestAsync(mf.Id, ct);
                            MergeOrAddCloudProject(list, projectFolder, cloudManifest);
                        }
                        catch (Exception fEx)
                        {
                            Console.WriteLine($"[GoogleDriveService] Avviso lettura cartella cloud {parentFolderId}: {fEx.Message}");
                        }
                    }

                    // 2b. Ricerca esplicita delle cartelle in "Condivisi con me" (sharedWithMe = true)
                    //     Garantisce il rilevamento anche se Google non ha indicizzato manifest.json per il collaboratore
                    var sharedFolders = await DriveListFilesAsync(
                        "sharedWithMe = true and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                        "id,name,owners,shared,modifiedTime",
                        100,
                        ct);

                    foreach (var folder in sharedFolders)
                    {
                        if (seenFolderIds.Contains(folder.Id)) continue;

                        try
                        {
                            var markers = await DriveListFilesAsync(
                                $"'{folder.Id}' in parents and (name = 'manifest.json' or (name = 'Central' and mimeType = 'application/vnd.google-apps.folder')) and trashed = false",
                                "id,name,mimeType,modifiedTime",
                                10,
                                ct);

                            if (markers.Count == 0) continue;

                            seenFolderIds.Add(folder.Id);
                            var mf = markers
                                .Where(m => string.Equals(m.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(m => m.ModifiedTime ?? DateTimeOffset.MinValue)
                                .FirstOrDefault();

                            var cloudManifest = mf != null ? await TryDownloadManifestAsync(mf.Id, ct) : null;
                            MergeOrAddCloudProject(list, folder, cloudManifest, forceSharedWithMe: true);
                        }
                        catch (Exception sEx)
                        {
                            Console.WriteLine($"[GoogleDriveService] Avviso scansione cartella condivisa '{folder.Name}': {sEx.Message}");
                        }
                    }

                    // 2c. Ricerca scorciatoie e sottocartelle nella cartella RevitProjects dell'utente
                    var revitProjectsFolders = await DriveListFilesAsync(
                        "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                        "id,name",
                        5,
                        ct);

                    if (revitProjectsFolders.Count > 0)
                    {
                        string rpId = revitProjectsFolders[0].Id;
                        var children = await DriveListFilesAsync(
                            $"'{rpId}' in parents and (mimeType = 'application/vnd.google-apps.folder' or mimeType = 'application/vnd.google-apps.shortcut') and trashed = false",
                            "id,name,mimeType,shortcutDetails",
                            100,
                            ct);

                        foreach (var child in children)
                        {
                            string targetId = (child.MimeType == "application/vnd.google-apps.shortcut" && !string.IsNullOrEmpty(child.ShortcutDetails?.TargetId))
                                ? child.ShortcutDetails.TargetId
                                : child.Id;

                            if (!seenFolderIds.Add(targetId)) continue;

                            try
                            {
                                var targetFolder = await DriveGetFileMetadataAsync(targetId, "id,name,owners,shared,modifiedTime", ct);
                                if (targetFolder == null) continue;

                                var markers = await DriveListFilesAsync(
                                    $"'{targetId}' in parents and (name = 'manifest.json' or (name = 'Central' and mimeType = 'application/vnd.google-apps.folder')) and trashed = false",
                                    "id,name,mimeType,modifiedTime",
                                    10,
                                    ct);

                                if (markers.Count == 0) continue;

                                var mf = markers
                                    .Where(m => string.Equals(m.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(m => m.ModifiedTime ?? DateTimeOffset.MinValue)
                                    .FirstOrDefault();

                                var cloudManifest = mf != null ? await TryDownloadManifestAsync(mf.Id, ct) : null;
                                MergeOrAddCloudProject(list, targetFolder, cloudManifest);
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GoogleDriveService] Scansione progetti cloud API: {ex.Message}");
                    if (IsAuthenticationError(ex))
                    {
                        InvalidateCredentialAndClearTokenStore();
                    }
                }
            }

            return list;
        }

        public async Task<string> MountCloudProjectAsync(string projectName, string cloudFolderId, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
            string localProjectDir = Path.Combine(_config.LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            Directory.CreateDirectory(localCentralDir);

            if (HasAuth && !string.IsNullOrEmpty(cloudFolderId))
            {
                try
                {
                    string targetParentId = "root";
                    var parentRes = await DriveListFilesAsync(
                        "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                        "id,name",
                        10,
                        ct);

                    if (parentRes.Count > 0)
                    {
                        targetParentId = parentRes[0].Id;
                    }

                    var existingRes = await DriveListFilesAsync(
                        $"'{targetParentId}' in parents and name = '{projectName.Replace("'", "\\'")}' and trashed = false",
                        "id",
                        5,
                        ct);

                    if (existingRes.Count == 0)
                    {
                        await DriveCreateMetadataOnlyAsync(new
                        {
                            name = projectName,
                            mimeType = "application/vnd.google-apps.shortcut",
                            shortcutDetails = new { targetId = cloudFolderId },
                            parents = new[] { targetParentId }
                        }, ct);
                    }
                }
                catch { }

                await DownloadCloudFolderRecursivelyAsync(cloudFolderId, localProjectDir, isInsideCentral: false, ct);
            }

            string? mountedCentral = FindCentralRvtFile(localCentralDir, projectName);
            if (!string.IsNullOrEmpty(mountedCentral))
            {
                return mountedCentral;
            }

            if (Directory.Exists(remoteProjectDir))
            {
                return await DownloadAndPrepareProjectAsync(projectName, ct);
            }

            throw new FileNotFoundException($"Impossibile preparare il modello centrale per '{projectName}'.");
        }

        private async Task DownloadCloudFolderRecursivelyAsync(string folderId, string localDir, bool isInsideCentral, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(localDir);

            var files = await DriveListFilesAsync(
                $"'{folderId}' in parents and trashed = false",
                "id,name,mimeType,size,md5Checksum,modifiedTime",
                1000,
                ct);

            var deduplicatedItems = files
                .Where(f => !IsIgnoredDirectoryName(f.Name) && !IsTransientTempFile(f.Name))
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).First())
                .ToList();

            var cloudNames = new HashSet<string>(deduplicatedItems.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

            foreach (var folder in deduplicatedItems.Where(f => f.MimeType == "application/vnd.google-apps.folder"))
            {
                ct.ThrowIfCancellationRequested();
                string localPath = Path.Combine(localDir, folder.Name);
                bool childInCentral = isInsideCentral || string.Equals(folder.Name, "Central", StringComparison.OrdinalIgnoreCase);
                await DownloadCloudFolderRecursivelyAsync(folder.Id, localPath, childInCentral, ct);
            }

            var fileItems = deduplicatedItems.Where(f => f.MimeType != "application/vnd.google-apps.folder").ToList();
            if (fileItems.Count > 0)
            {
                using var sem = new SemaphoreSlim(6);
                var downloadTasks = fileItems.Select(async item =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        string localPath = Path.Combine(localDir, item.Name);

                        if (File.Exists(localPath) && !string.Equals(item.Name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                        {
                            var localFi = new FileInfo(localPath);
                            if (localFi.Length == (item.Size ?? -1) && !string.IsNullOrEmpty(item.Md5Checksum))
                            {
                                string localMd5 = await FileHashHelper.ComputeMd5Async(localPath, ct);
                                if (string.Equals(localMd5, item.Md5Checksum, StringComparison.OrdinalIgnoreCase))
                                {
                                    return;
                                }
                            }
                        }

                        string tempDownload = localPath + ".dl." + Guid.NewGuid().ToString("N");
                        try
                        {
                            using (var fileStream = new FileStream(tempDownload, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await DriveDownloadFileAsync(item.Id, fileStream, ct);
                            }
                            File.Move(tempDownload, localPath, true);
                        }
                        finally
                        {
                            if (File.Exists(tempDownload))
                            {
                                try { File.Delete(tempDownload); } catch { }
                            }
                        }
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(downloadTasks);
            }

            if (isInsideCentral)
            {
                foreach (var existingFile in Directory.GetFiles(localDir))
                {
                    string fName = Path.GetFileName(existingFile);
                    if (fName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!cloudNames.Contains(fName))
                    {
                        try { File.Delete(existingFile); } catch { }
                    }
                }
            }
        }

        private async Task UploadCloudFolderRecursivelyAsync(
            string localDir,
            string parentFolderId,
            bool isInsideCentral,
            CancellationToken ct,
            bool? deleteMissingOverride = null)
        {
            if (!Directory.Exists(localDir)) return;
            ct.ThrowIfCancellationRequested();

            bool deleteMissingFiles = deleteMissingOverride ?? isInsideCentral;

            var files = await DriveListFilesAsync(
                $"'{parentFolderId}' in parents and trashed = false",
                "id,name,mimeType,size,md5Checksum,modifiedTime",
                1000,
                ct);

            var groupedByName = files
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            var cloudMap = groupedByName.ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var latest = kvp.Value[0];
                    return (id: latest.Id, mimeType: latest.MimeType, size: latest.Size ?? -1, md5: latest.Md5Checksum ?? "");
                },
                StringComparer.OrdinalIgnoreCase);

            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var subDir in Directory.GetDirectories(localDir))
            {
                ct.ThrowIfCancellationRequested();
                string subName = Path.GetFileName(subDir);
                if (IsIgnoredDirectoryName(subName)) continue;
                localNames.Add(subName);

                string subFolderId;
                if (cloudMap.TryGetValue(subName, out var entry) && entry.mimeType == "application/vnd.google-apps.folder")
                {
                    subFolderId = entry.id;
                }
                else
                {
                    var created = await DriveCreateMetadataOnlyAsync(new
                    {
                        name = subName,
                        mimeType = "application/vnd.google-apps.folder",
                        parents = new[] { parentFolderId }
                    }, ct);
                    subFolderId = created.Id;
                }

                bool childInCentral = isInsideCentral || string.Equals(subName, "Central", StringComparison.OrdinalIgnoreCase);
                await UploadCloudFolderRecursivelyAsync(subDir, subFolderId, childInCentral, ct, deleteMissingOverride);
            }

            string? deferredManifestPath = null;
            var normalFiles = new List<string>();

            foreach (var localFile in Directory.GetFiles(localDir))
            {
                string fileName = Path.GetFileName(localFile);
                if (IsTransientTempFile(fileName)) continue;
                localNames.Add(fileName);

                if (!isInsideCentral && string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    deferredManifestPath = localFile;
                }
                else
                {
                    normalFiles.Add(localFile);
                }
            }

            async Task UploadSingleFileAsync(string localFile)
            {
                string fileName = Path.GetFileName(localFile);
                var fi = new FileInfo(localFile);

                if (cloudMap.TryGetValue(fileName, out var existingEntry))
                {
                    bool contentUpdated = false;
                    if (existingEntry.size == fi.Length &&
                        !string.IsNullOrEmpty(existingEntry.md5) &&
                        !string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase))
                    {
                        string localMd5 = await FileHashHelper.ComputeMd5Async(localFile, ct);
                        if (string.Equals(localMd5, existingEntry.md5, StringComparison.OrdinalIgnoreCase))
                        {
                            contentUpdated = true;
                        }
                    }

                    if (!contentUpdated)
                    {
                        using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await DriveUpdateFileContentAsync(existingEntry.id, fs, ct);
                    }

                    // Rimuove eventuali duplicati con lo stesso nome sulla cartella Drive
                    if (groupedByName.TryGetValue(fileName, out var allMatches) && allMatches.Count > 1)
                    {
                        foreach (var dup in allMatches.Skip(1))
                        {
                            if (dup.MimeType != "application/vnd.google-apps.folder")
                            {
                                try { await DriveDeleteFileAsync(dup.Id, ct); } catch { }
                            }
                        }
                    }
                }
                else
                {
                    using var fs = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    await DriveCreateAndUploadFileAsync(fileName, parentFolderId, fs, ct);
                }
            }

            if (normalFiles.Count > 0)
            {
                using var sem = new SemaphoreSlim(4);
                var uploadTasks = normalFiles.Select(async f =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        await UploadSingleFileAsync(f);
                    }
                    finally
                    {
                        sem.Release();
                    }
                });

                await Task.WhenAll(uploadTasks);
            }

            if (deleteMissingFiles)
            {
                foreach (var kvp in cloudMap)
                {
                    if (kvp.Value.mimeType != "application/vnd.google-apps.folder" && !localNames.Contains(kvp.Key))
                    {
                        try
                        {
                            await DriveDeleteFileAsync(kvp.Value.id, ct);
                        }
                        catch { }
                    }
                }
            }

            if (deferredManifestPath != null)
            {
                await UploadSingleFileAsync(deferredManifestPath);
            }
        }

        public Task<string> DownloadAndPrepareProjectAsync(string projectName, CancellationToken ct = default)
            => DownloadAndPrepareProjectAsync(projectName, null, ct);

        public async Task<string> DownloadAndPrepareProjectAsync(string projectName, string? preferredCloudFolderId, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string localProjectDir = Path.Combine(_config.LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            string? cloudFolderId = preferredCloudFolderId;
            Directory.CreateDirectory(localCentralDir);

            bool apiDownloadCompleted = false;
            if (HasAuth)
            {
                if (string.IsNullOrEmpty(cloudFolderId))
                {
                    cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                }
                if (!string.IsNullOrEmpty(cloudFolderId))
                {
                    Console.WriteLine($"[GoogleDriveService] Download e allineamento da Google Drive ({cloudFolderId})...");
                    await DownloadCloudFolderRecursivelyAsync(cloudFolderId, localProjectDir, isInsideCentral: false, ct);
                    apiDownloadCompleted = true;
                }
            }

            if (!apiDownloadCompleted)
            {
                string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
                string remoteCentralDir = Path.Combine(remoteProjectDir, "Central");
                if (Directory.Exists(remoteCentralDir))
                {
                    try
                    {
                        await SyncDirectoryAsync(remoteCentralDir, localCentralDir, cleanRemovedFiles: true, ct);
                        string remoteLinksDir = Path.Combine(remoteProjectDir, "Links");
                        string localLinksDir = Path.Combine(localProjectDir, "Links");
                        if (Directory.Exists(remoteLinksDir))
                        {
                            await SyncDirectoryAsync(remoteLinksDir, localLinksDir, cleanRemovedFiles: false, ct);
                        }
                        string remoteManifest = Path.Combine(remoteProjectDir, "manifest.json");
                        string localManifest = Path.Combine(localProjectDir, "manifest.json");
                        if (File.Exists(remoteManifest))
                        {
                            File.Copy(remoteManifest, localManifest, true);
                        }
                    }
                    catch { }
                }
            }

            string? preparedCentral = FindCentralRvtFile(localCentralDir, projectName);
            if (string.IsNullOrEmpty(preparedCentral))
                throw new FileNotFoundException($"Nessun modello centrale trovato per il progetto '{projectName}'.");

            return preparedCentral;
        }

        public bool ShouldRequireGoogleAuth(string projectName)
        {
            if (IsLocalFallbackStorageOnly()) return true;

            try
            {
                string localManifest = Path.Combine(_config.LocalCachePath, projectName, "manifest.json");
                if (File.Exists(localManifest))
                {
                    var m = JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(localManifest));
                    if (!string.IsNullOrEmpty(m?.CloudFolderId))
                    {
                        string remoteDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, m.CloudFolderId);
                        if (!Directory.Exists(remoteDir) || !File.Exists(Path.Combine(remoteDir, "manifest.json")))
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private async Task<ProjectManifest?> TryGetCloudManifestFromFolderAsync(string cloudFolderId, CancellationToken ct)
        {
            var mFiles = await DriveListFilesAsync(
                $"'{cloudFolderId}' in parents and name = 'manifest.json' and trashed = false",
                "id,name,modifiedTime",
                10,
                ct);

            var latestMf = mFiles
                .OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

            if (latestMf == null) return null;

            using var mStream = new MemoryStream();
            await DriveDownloadFileAsync(latestMf.Id, mStream, ct);
            string cloudJson = Encoding.UTF8.GetString(mStream.ToArray());
            return JsonSerializer.Deserialize<ProjectManifest>(cloudJson);
        }

        public async Task UploadProjectChangesAsync(
            string projectName,
            string localCentralFilePath,
            string user,
            long? expectedBaseVersion = null,
            string? expectedBaseHash = null,
            bool isInitialCreate = false,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReloadConfig();
            await EnsureGoogleConnectedIfNeededAsync(ct);

            if (!HasAuth && ShouldRequireGoogleAuth(projectName))
            {
                throw new InvalidOperationException(
                    "Account Google non collegato o sessione scaduta. Impossibile verificare la versione cloud prima del caricamento.");
            }

            string localCentralDir = Path.GetDirectoryName(localCentralFilePath)!;
            string localProjectDir = Path.GetDirectoryName(localCentralDir)!;
            string? cloudFolderId = HasAuth
                ? await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct)
                : null;

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
            string remoteCentralDir = Path.Combine(remoteProjectDir, "Central");

            string localManifestFile = Path.Combine(localProjectDir, "manifest.json");
            ProjectManifest? localOldManifest = null;
            long localOldVersion = 0;
            if (File.Exists(localManifestFile))
            {
                try
                {
                    var oldJson = await File.ReadAllTextAsync(localManifestFile, ct);
                    localOldManifest = JsonSerializer.Deserialize<ProjectManifest>(oldJson);
                    if (localOldManifest != null)
                    {
                        localOldVersion = localOldManifest.Version;
                    }
                }
                catch { }
            }

            ProjectManifest? cloudOldManifest = null;
            if (HasAuth && !string.IsNullOrEmpty(cloudFolderId))
            {
                cloudOldManifest = await TryGetCloudManifestFromFolderAsync(cloudFolderId, ct);

                if (isInitialCreate)
                {
                    if (cloudOldManifest != null)
                    {
                        throw new InvalidOperationException(
                            $"Esiste già un progetto cloud chiamato '{projectName}' su Google Drive (v{cloudOldManifest.Version}, ultimo agg. di {cloudOldManifest.LastSyncUser}).\nPer non sovrascrivere il lavoro del team, usa 'Apri da Google Drive' invece di 'Pubblica su Drive'.");
                    }

                    string? existingCloudCentralId = await GetSubfolderIdAsync(cloudFolderId, "Central", createIfMissing: false, ct);
                    if (!string.IsNullOrEmpty(existingCloudCentralId))
                    {
                        var existingCentralFiles = await DriveListFilesAsync(
                            $"'{existingCloudCentralId}' in parents and trashed = false",
                            "id,name",
                            10,
                            ct);
                        if (existingCentralFiles.Any(f => f.Name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)))
                        {
                            throw new InvalidOperationException(
                                $"Esiste già una cartella progetto '{projectName}' con modello centrale su Google Drive. Usa 'Apri da Google Drive' invece di 'Pubblica su Drive'.");
                        }
                    }
                }
            }
            else if (!HasAuth && !IsLocalFallbackStorageOnly())
            {
                string remoteManifestPath = Path.Combine(remoteProjectDir, "manifest.json");
                if (File.Exists(remoteManifestPath))
                {
                    string remoteOldJson = await File.ReadAllTextAsync(remoteManifestPath, ct);
                    cloudOldManifest = JsonSerializer.Deserialize<ProjectManifest>(remoteOldJson);
                }

                if (isInitialCreate && (cloudOldManifest != null || !string.IsNullOrEmpty(FindCentralRvtFile(remoteCentralDir, projectName))))
                {
                    throw new InvalidOperationException(
                        $"Esiste già un progetto '{projectName}' nella cartella condivisa. Usa 'Apri da Google Drive' invece di 'Pubblica su Drive'.");
                }
            }

            // Verifica ottimistica anti-sovrascrittura prima di toccare i file su cloud
            if (!isInitialCreate)
            {
                if (cloudOldManifest != null)
                {
                    if (expectedBaseVersion.HasValue && cloudOldManifest.Version != expectedBaseVersion.Value)
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: il modello su Google Drive è stato aggiornato alla v{cloudOldManifest.Version} da '{cloudOldManifest.LastSyncUser}' mentre la tua operazione era basata sulla v{expectedBaseVersion.Value}. Caricamento annullato per non sovrascrivere le modifiche del collega.");
                    }

                    if (!expectedBaseVersion.HasValue && localOldVersion > 0 && cloudOldManifest.Version != localOldVersion)
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: il modello su Google Drive è alla v{cloudOldManifest.Version} (di '{cloudOldManifest.LastSyncUser}'), mentre la tua cache locale era alla v{localOldVersion}. Caricamento annullato per proteggere i dati su cloud.");
                    }

                    if (!string.IsNullOrEmpty(expectedBaseHash) &&
                        !string.IsNullOrEmpty(cloudOldManifest.CentralFileSha256) &&
                        !string.Equals(expectedBaseHash, cloudOldManifest.CentralFileSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: l'hash del modello centrale su Google Drive è cambiato rispetto alla verifica pre-sincronizzazione (ultimo agg. di '{cloudOldManifest.LastSyncUser}'). Caricamento annullato per evitare sovrascritture.");
                    }

                    if (!expectedBaseVersion.HasValue &&
                        localOldManifest != null &&
                        !string.IsNullOrEmpty(localOldManifest.CentralFileSha256) &&
                        !string.IsNullOrEmpty(cloudOldManifest.CentralFileSha256) &&
                        !string.Equals(localOldManifest.CentralFileSha256, cloudOldManifest.CentralFileSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"CONFLITTO RILEVATO: il modello su Google Drive (ultimo agg. di '{cloudOldManifest.LastSyncUser}') non corrisponde più alla copia su cui stai lavorando. Esegui una nuova sincronizzazione.");
                    }
                }
                else if (expectedBaseVersion.HasValue && expectedBaseVersion.Value > 0)
                {
                    throw new InvalidOperationException(
                        $"CONFLITTO RILEVATO: manifest.json non trovato su Google Drive (attesa v{expectedBaseVersion.Value}). Caricamento annullato per sicurezza.");
                }
            }

            if (HasAuth && string.IsNullOrEmpty(cloudFolderId))
            {
                cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: true, ct);
                remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
                remoteCentralDir = Path.Combine(remoteProjectDir, "Central");
            }

            string centralHash = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
            var centralFileInfo = new FileInfo(localCentralFilePath);

            long cloudOldVersion = cloudOldManifest?.Version ?? 0;

            var manifest = new ProjectManifest
            {
                ProjectId = localOldManifest?.ProjectId ?? projectName,
                ProjectName = projectName,
                CloudFolderId = !string.IsNullOrEmpty(cloudFolderId) ? cloudFolderId : localOldManifest?.CloudFolderId,
                LastSyncUtc = DateTime.UtcNow,
                LastSyncUser = user,
                LastSyncMachine = Environment.MachineName,
                CentralFileSha256 = centralHash,
                CentralFileSizeBytes = centralFileInfo.Length,
                Version = Math.Max(localOldVersion, cloudOldVersion) + 1,
                ActiveUsers = await GetActiveUsersAsync(projectName, ct)
            };

            foreach (var f in Directory.GetFiles(localCentralDir, "*", SearchOption.AllDirectories))
            {
                string relFromCentral = Path.GetRelativePath(localCentralDir, f);
                string fileName = Path.GetFileName(f);
                if (ContainsPathSegment(relFromCentral, "Revit_temp") || IsTransientTempFile(fileName))
                {
                    continue;
                }

                var fi = new FileInfo(f);
                manifest.Files.Add(new FileEntry
                {
                    RelativePath = Path.GetRelativePath(localProjectDir, f),
                    Sha256 = await FileHashHelper.ComputeSha256Async(f, ct),
                    SizeBytes = fi.Length,
                    LastModifiedUtc = fi.LastWriteTimeUtc
                });
            }

            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(localManifestFile, manifestJson, ct);

            // 1. Upload diretto tramite Google Drive API v3
            if (HasAuth)
            {
                if (string.IsNullOrEmpty(cloudFolderId))
                {
                    throw new InvalidOperationException($"Impossibile creare o accedere alla cartella '{projectName}' su Google Drive.");
                }

                Console.WriteLine($"[GoogleDriveService] Upload modifiche su Google Drive ({cloudFolderId})...");
                await UploadCloudFolderRecursivelyAsync(localProjectDir, cloudFolderId, isInsideCentral: false, ct);
                Console.WriteLine($"[GoogleDriveService] Upload API completato con successo!");
            }

            // 2. Allinea anche la cartella CloudStoragePath se è un disco Google Drive Desktop reale (non fallback locale)
            if (!HasAuth || !IsLocalFallbackStorageOnly())
            {
                try
                {
                    if (Directory.Exists(_config.CloudStoragePath))
                    {
                        Directory.CreateDirectory(remoteCentralDir);
                        await SyncDirectoryAsync(localCentralDir, remoteCentralDir, cleanRemovedFiles: true, ct);

                        string localLinksDir = Path.Combine(localProjectDir, "Links");
                        string remoteLinksDir = Path.Combine(remoteProjectDir, "Links");
                        if (Directory.Exists(localLinksDir))
                        {
                            await SyncDirectoryAsync(localLinksDir, remoteLinksDir, cleanRemovedFiles: false, ct);
                        }

                        string remoteManifestFile = Path.Combine(remoteProjectDir, "manifest.json");
                        await File.WriteAllTextAsync(remoteManifestFile, manifestJson, ct);
                    }
                }
                catch { }
            }

            Console.WriteLine($"[GoogleDriveService] Progetto '{projectName}' sincronizzato (Versione {manifest.Version}) da {user}.");
        }

        public async Task UploadProjectLinksAsync(string projectName, string localLinksDir, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(localLinksDir)) return;

            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            if (HasAuth)
            {
                string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (!string.IsNullOrEmpty(cloudFolderId))
                {
                    string? cloudLinksId = await GetSubfolderIdAsync(cloudFolderId, "Links", createIfMissing: true, ct);
                    if (!string.IsNullOrEmpty(cloudLinksId))
                    {
                        await UploadCloudFolderRecursivelyAsync(localLinksDir, cloudLinksId, isInsideCentral: false, ct, deleteMissingOverride: false);
                    }
                }
            }

            if (!HasAuth || !IsLocalFallbackStorageOnly())
            {
                try
                {
                    string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName);
                    if (Directory.Exists(remoteProjectDir))
                    {
                        string remoteLinksDir = Path.Combine(remoteProjectDir, "Links");
                        await SyncDirectoryAsync(localLinksDir, remoteLinksDir, cleanRemovedFiles: false, ct);
                    }
                }
                catch { }
            }
        }

        private async Task<bool> CheckCentralHashMatchesAsync(string localCentralDir, string? projectName, string expectedHash, string computedStateHash, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(expectedHash))
            {
                return !string.IsNullOrEmpty(FindCentralRvtFile(localCentralDir, projectName));
            }

            if (string.Equals(computedStateHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Compatibilità stretta con manifest legacy che salvavano solo lo SHA-256 del file .rvt
            string? rvtFile = FindCentralRvtFile(localCentralDir, projectName);
            if (!string.IsNullOrEmpty(rvtFile) && File.Exists(rvtFile))
            {
                string rawRvtSha = await FileHashHelper.ComputeSha256Async(rvtFile, ct);
                if (string.Equals(rawRvtSha, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public async Task<SyncIntegrityResult> VerifyIntegrityAndAlignAsync(string projectName, string user, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string localProjectDir = Path.Combine(_config.LocalCachePath, projectName);
            string localCentralDir = Path.Combine(localProjectDir, "Central");
            string? cloudFolderId = null;
            string localManifestFile = Path.Combine(localProjectDir, "manifest.json");

            long localVersion = 0;
            if (File.Exists(localManifestFile))
            {
                try
                {
                    string localJson = await File.ReadAllTextAsync(localManifestFile, ct);
                    var localManifest = JsonSerializer.Deserialize<ProjectManifest>(localJson);
                    if (localManifest != null)
                    {
                        localVersion = localManifest.Version;
                    }
                }
                catch { }
            }

            if (HasAuth)
            {
                try
                {
                    cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudFolderId))
                    {
                        return new SyncIntegrityResult
                        {
                            IsAligned = false,
                            IsWaitingForDownload = false,
                            Message = $"Progetto '{projectName}' non trovato su Google Drive."
                        };
                    }

                    var mFiles = await DriveListFilesAsync(
                        $"'{cloudFolderId}' in parents and name = 'manifest.json' and trashed = false",
                        "id,name,modifiedTime",
                        10,
                        ct);

                    var latestMf = mFiles
                        .OrderByDescending(x => x.ModifiedTime ?? DateTimeOffset.MinValue)
                        .FirstOrDefault();

                    if (latestMf != null)
                    {
                        using var mStream = new MemoryStream();
                        await DriveDownloadFileAsync(latestMf.Id, mStream, ct);
                        string cloudJson = Encoding.UTF8.GetString(mStream.ToArray());
                        var cloudManifest = JsonSerializer.Deserialize<ProjectManifest>(cloudJson);

                        if (cloudManifest == null)
                        {
                            return new SyncIntegrityResult
                            {
                                IsAligned = false,
                                IsWaitingForDownload = false,
                                Message = "Manifest cloud su Google Drive non leggibile."
                            };
                        }

                        string localHashBefore = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
                        if (!string.IsNullOrEmpty(cloudManifest.CentralFileSha256) &&
                            await CheckCentralHashMatchesAsync(localCentralDir, projectName, cloudManifest.CentralFileSha256, localHashBefore, ct) &&
                            localVersion == cloudManifest.Version)
                        {
                            return new SyncIntegrityResult
                            {
                                IsAligned = true,
                                WasUpdatedFromCloud = false,
                                CloudVersion = cloudManifest.Version,
                                LastSyncUser = cloudManifest.LastSyncUser,
                                ExpectedHash = cloudManifest.CentralFileSha256,
                                CurrentLocalHash = localHashBefore,
                                Message = "Modello locale già allineato all'ultima versione."
                            };
                        }

                        await DownloadCloudFolderRecursivelyAsync(cloudFolderId, localProjectDir, isInsideCentral: false, ct);
                        await File.WriteAllTextAsync(localManifestFile, cloudJson, ct);

                        string localHashAfter = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
                        bool hashMatches = await CheckCentralHashMatchesAsync(localCentralDir, projectName, cloudManifest.CentralFileSha256, localHashAfter, ct);

                        return new SyncIntegrityResult
                        {
                            IsAligned = hashMatches,
                            IsWaitingForDownload = !hashMatches,
                            WasUpdatedFromCloud = hashMatches,
                            CloudVersion = cloudManifest.Version,
                            LastSyncUser = cloudManifest.LastSyncUser,
                            ExpectedHash = cloudManifest.CentralFileSha256,
                            CurrentLocalHash = localHashAfter,
                            Message = hashMatches
                                ? $"Allineamento completato con successo alla versione {cloudManifest.Version} di {cloudManifest.LastSyncUser}."
                                : "Verifica integrità modello centrale non riuscita dopo il download da Google Drive (hash non corrispondente)."
                        };
                    }
                    else
                    {
                        // Progetto su Drive senza manifest.json (es. creato manualmente): verifica cartella Central
                        string? cloudCentralId = await GetSubfolderIdAsync(cloudFolderId, "Central", createIfMissing: false, ct);
                        if (string.IsNullOrEmpty(cloudCentralId))
                        {
                            return new SyncIntegrityResult
                            {
                                IsAligned = false,
                                IsWaitingForDownload = false,
                                Message = $"Cartella 'Central' non trovata su Google Drive per '{projectName}'."
                            };
                        }

                        await DownloadCloudFolderRecursivelyAsync(cloudFolderId, localProjectDir, isInsideCentral: false, ct);
                        bool hasCentral = !string.IsNullOrEmpty(FindCentralRvtFile(localCentralDir, projectName));
                        return new SyncIntegrityResult
                        {
                            IsAligned = hasCentral,
                            IsWaitingForDownload = false,
                            WasUpdatedFromCloud = hasCentral,
                            CloudVersion = 0,
                            Message = hasCentral
                                ? "Modello centrale allineato da Google Drive (senza manifest precedente)."
                                : "Nessun file .rvt trovato nella cartella Central su Google Drive."
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception apiEx)
                {
                    Console.WriteLine($"[GoogleDriveService] Errore controllo integrità API: {apiEx.Message}");
                    return new SyncIntegrityResult
                    {
                        IsAligned = false,
                        IsWaitingForDownload = false,
                        Message = $"Errore di connessione a Google Drive: {apiEx.Message}"
                    };
                }
            }

            if (ShouldRequireGoogleAuth(projectName))
            {
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = false,
                    Message = "Account Google non connesso o sessione scaduta. Apri 'Account Google' e accedi prima di sincronizzare."
                };
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
            string manifestFile = Path.Combine(remoteProjectDir, "manifest.json");
            string remoteCentralDir = Path.Combine(remoteProjectDir, "Central");

            if (!Directory.Exists(remoteProjectDir) || !Directory.Exists(remoteCentralDir))
            {
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = true,
                    Message = "In attesa della cartella Central su Google Drive Desktop..."
                };
            }

            if (!File.Exists(manifestFile))
            {
                if (File.Exists(localManifestFile))
                {
                    return new SyncIntegrityResult
                    {
                        IsAligned = false,
                        IsWaitingForDownload = true,
                        Message = "In attesa di manifest.json da Google Drive Desktop..."
                    };
                }

                await SyncDirectoryAsync(remoteCentralDir, localCentralDir, cleanRemovedFiles: true, ct);
                bool hasCentral = !string.IsNullOrEmpty(FindCentralRvtFile(localCentralDir, projectName));
                return new SyncIntegrityResult
                {
                    IsAligned = hasCentral,
                    IsWaitingForDownload = !hasCentral,
                    WasUpdatedFromCloud = hasCentral,
                    Message = hasCentral ? "Allineato dalla cartella condivisa." : "File centrale non trovato nella cartella condivisa."
                };
            }

            ProjectManifest? manifest = null;
            try
            {
                string json = await File.ReadAllTextAsync(manifestFile, ct);
                manifest = JsonSerializer.Deserialize<ProjectManifest>(json);
            }
            catch { }

            if (manifest == null)
            {
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = true,
                    Message = "In attesa del completamento scrittura manifest.json su Google Drive..."
                };
            }

            string currentLocalHash = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
            if (!string.IsNullOrEmpty(manifest.CentralFileSha256) &&
                await CheckCentralHashMatchesAsync(localCentralDir, projectName, manifest.CentralFileSha256, currentLocalHash, ct) &&
                localVersion == manifest.Version)
            {
                return new SyncIntegrityResult
                {
                    IsAligned = true,
                    WasUpdatedFromCloud = false,
                    CloudVersion = manifest.Version,
                    LastSyncUser = manifest.LastSyncUser,
                    ExpectedHash = manifest.CentralFileSha256,
                    CurrentLocalHash = currentLocalHash,
                    Message = "Modello locale già allineato all'ultima versione."
                };
            }

            string remoteHash = await ComputeCentralStateHashAsync(remoteCentralDir, projectName, ct);
            if (!string.IsNullOrEmpty(manifest.CentralFileSha256) &&
                !await CheckCentralHashMatchesAsync(remoteCentralDir, projectName, manifest.CentralFileSha256, remoteHash, ct))
            {
                return new SyncIntegrityResult
                {
                    IsAligned = false,
                    IsWaitingForDownload = true,
                    CloudVersion = manifest.Version,
                    LastSyncUser = manifest.LastSyncUser,
                    ExpectedHash = manifest.CentralFileSha256,
                    CurrentLocalHash = remoteHash,
                    Message = $"Download Google Drive in corso per la versione {manifest.Version} di {manifest.LastSyncUser}..."
                };
            }

            await SyncDirectoryAsync(remoteCentralDir, localCentralDir, cleanRemovedFiles: true, ct);
            try { File.Copy(manifestFile, localManifestFile, true); } catch { }

            string alignedHash = await ComputeCentralStateHashAsync(localCentralDir, projectName, ct);
            bool alignedOk = await CheckCentralHashMatchesAsync(localCentralDir, projectName, manifest.CentralFileSha256, alignedHash, ct);

            return new SyncIntegrityResult
            {
                IsAligned = alignedOk,
                IsWaitingForDownload = !alignedOk,
                WasUpdatedFromCloud = alignedOk,
                CloudVersion = manifest.Version,
                LastSyncUser = manifest.LastSyncUser,
                ExpectedHash = manifest.CentralFileSha256,
                CurrentLocalHash = alignedHash,
                Message = alignedOk
                    ? $"Allineamento completato con successo alla versione {manifest.Version} di {manifest.LastSyncUser}."
                    : "In attesa del completamento copia locale..."
            };
        }

        private static readonly HashSet<string> _volatileBackupFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "editingperms.dat",
            "editingdeltas.dat",
            "editingrequests.dat",
            "users.dat",
            "worksetperms.dat"
        };

        private static bool IsIgnoredDirectoryName(string dirName)
        {
            return string.Equals(dirName, "Local", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, "Revit_temp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(dirName, ".connector", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTransientTempFile(string fileName)
        {
            return fileName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Contains(".dl.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsPathSegment(string relPath, string segmentName)
        {
            var parts = relPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(p => string.Equals(p, segmentName, StringComparison.OrdinalIgnoreCase));
        }

        private static string? FindCentralRvtFile(string centralDir, string? projectName = null)
        {
            if (!Directory.Exists(centralDir)) return null;

            if (!string.IsNullOrWhiteSpace(projectName))
            {
                string expectedPath = Path.Combine(centralDir, $"{projectName}_Central.rvt");
                if (File.Exists(expectedPath))
                {
                    return expectedPath;
                }
            }

            var centralCandidates = Directory.GetFiles(centralDir, "*_Central.rvt");
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                string expectedName = $"{projectName}_Central.rvt";
                var caseInsensitiveMatch = centralCandidates.FirstOrDefault(f =>
                    string.Equals(Path.GetFileName(f), expectedName, StringComparison.OrdinalIgnoreCase));
                if (caseInsensitiveMatch != null) return caseInsensitiveMatch;
            }

            if (centralCandidates.Length > 0)
            {
                return centralCandidates
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .First();
            }

            var rvtCandidates = Directory.GetFiles(centralDir, "*.rvt");
            if (rvtCandidates.Length > 0)
            {
                return rvtCandidates
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .First();
            }

            return null;
        }

        private static Task<string> GetLocalCentralHashAsync(string centralDir, CancellationToken ct)
            => ComputeCentralStateHashAsync(centralDir, null, ct);

        private static async Task<string> ComputeCentralStateHashAsync(string centralDir, string? projectName, CancellationToken ct)
        {
            string? centralRvtFile = FindCentralRvtFile(centralDir, projectName);
            if (string.IsNullOrEmpty(centralRvtFile) || !File.Exists(centralRvtFile))
            {
                return string.Empty;
            }

            try
            {
                string expectedBackupDir = Path.Combine(
                    centralDir,
                    Path.GetFileNameWithoutExtension(centralRvtFile) + "_backup");

                string? backupDir = Directory.Exists(expectedBackupDir)
                    ? expectedBackupDir
                    : Directory.GetDirectories(centralDir, "*_Central_backup")
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();

                if (!string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir))
                {
                    var versionedFiles = Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            string rel = Path.GetRelativePath(backupDir, f);
                            if (ContainsPathSegment(rel, "Revit_temp")) return false;

                            string fileName = Path.GetFileName(f);
                            if (_volatileBackupFiles.Contains(fileName)) return false;
                            if (IsTransientTempFile(fileName)) return false;

                            string ext = Path.GetExtension(fileName);
                            return string.Equals(ext, ".rws", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ext, ".dat", StringComparison.OrdinalIgnoreCase);
                        })
                        .OrderBy(f => Path.GetRelativePath(backupDir, f), StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (versionedFiles.Count > 0)
                    {
                        var rvtInfo = new FileInfo(centralRvtFile);
                        var sb = new StringBuilder();
                        sb.Append("RVT:").Append(rvtInfo.Length).Append(';');

                        foreach (var f in versionedFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            var fi = new FileInfo(f);
                            string relPath = Path.GetRelativePath(backupDir, f).Replace('\\', '/');
                            string md5 = await FileHashHelper.ComputeMd5Async(f, ct);
                            sb.Append(relPath).Append(':').Append(fi.Length).Append(':').Append(md5).Append(';');
                        }

                        using var sha256 = System.Security.Cryptography.SHA256.Create();
                        byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    }
                }

                return await FileHashHelper.ComputeSha256Async(centralRvtFile, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return string.Empty;
            }
        }

        public async Task CreateCloudProjectAsync(string projectName, string localCentralFilePath, string user, CancellationToken ct = default)
        {
            await UploadProjectChangesAsync(
                projectName,
                localCentralFilePath,
                user,
                expectedBaseVersion: 0,
                expectedBaseHash: null,
                isInitialCreate: true,
                ct: ct);
        }

        private static bool IsSyncLockFileName(string name)
        {
            return string.Equals(name, "sync_lock.json", StringComparison.OrdinalIgnoreCase) ||
                   (name.StartsWith("sync_lock_", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<List<(DriveFileItem driveItem, FileLockData data)>> ReadCloudLocksAsync(
            string cloudConnectorId,
            DateTime now,
            int defaultTimeoutSeconds,
            bool deleteExpired,
            CancellationToken ct)
        {
            var items = await DriveListFilesAsync(
                $"'{cloudConnectorId}' in parents and trashed = false",
                "id,name,mimeType,createdTime,modifiedTime",
                100,
                ct);

            var active = new List<(DriveFileItem driveItem, FileLockData data)>();
            foreach (var item in items.Where(f => f.MimeType != "application/vnd.google-apps.folder" && IsSyncLockFileName(f.Name)))
            {
                try
                {
                    using var ms = new MemoryStream();
                    await DriveDownloadFileAsync(item.Id, ms, ct);
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    var data = JsonSerializer.Deserialize<FileLockData>(json);
                    if (data == null)
                    {
                        if (deleteExpired)
                        {
                            try { await DriveDeleteFileAsync(item.Id, ct); } catch { }
                        }
                        continue;
                    }

                    int tolerance = data.SkewToleranceSeconds > 0 ? data.SkewToleranceSeconds : FileBasedLockManager.DefaultSkewToleranceSeconds;
                    bool isClockSkewExpired = data.AcquiredAtUtc > now.AddSeconds(60) &&
                                              item.ModifiedTime.HasValue &&
                                              now > item.ModifiedTime.Value.UtcDateTime.AddSeconds(defaultTimeoutSeconds + tolerance);

                    if (!isClockSkewExpired && data.ExpiresAtUtc.AddSeconds(tolerance) > now)
                    {
                        active.Add((item, data));
                    }
                    else if (deleteExpired)
                    {
                        try { await DriveDeleteFileAsync(item.Id, ct); } catch { }
                    }
                }
                catch
                {
                    if (deleteExpired && item.ModifiedTime.HasValue &&
                        now > item.ModifiedTime.Value.UtcDateTime.AddSeconds(defaultTimeoutSeconds + FileBasedLockManager.DefaultSkewToleranceSeconds))
                    {
                        try { await DriveDeleteFileAsync(item.Id, ct); } catch { }
                    }
                }
            }

            return active;
        }

        public async Task<LockResponse> AcquireProjectLockAsync(LockRequest lockReq, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            if (!HasAuth && ShouldRequireGoogleAuth(lockReq.ProjectId))
            {
                return new LockResponse
                {
                    Status = SyncLockStatus.Error,
                    Message = "Account Google non connesso o sessione scaduta. Apri 'Account Google' e accedi prima di sincronizzare."
                };
            }

            if (HasAuth)
            {
                try
                {
                    string? cloudFolderId = await GetProjectFolderIdAsync(lockReq.ProjectId, createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudFolderId))
                    {
                        return new LockResponse
                        {
                            Status = SyncLockStatus.Error,
                            Message = $"Progetto '{lockReq.ProjectId}' non trovato su Google Drive."
                        };
                    }

                    string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: true, ct);
                    if (string.IsNullOrEmpty(cloudConnectorId))
                    {
                        return new LockResponse
                        {
                            Status = SyncLockStatus.Error,
                            Message = "Impossibile accedere alla cartella '.connector' su Google Drive."
                        };
                    }

                    string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, lockReq.ProjectId, cloudFolderId);
                    var now = NetworkTimeHelper.GetAccurateUtc();

                    // 1. Verifica eventuali lock già attivi su Google Drive
                    var existingLocks = await ReadCloudLocksAsync(cloudConnectorId, now, lockReq.TimeoutSeconds, deleteExpired: true, ct);
                    var otherHolder = existingLocks
                        .Select(x => x.data)
                        .FirstOrDefault(l => !(l.User.Equals(lockReq.User, StringComparison.OrdinalIgnoreCase) &&
                                               l.Machine.Equals(lockReq.Machine, StringComparison.OrdinalIgnoreCase)));

                    if (otherHolder != null)
                    {
                        int tolerance = otherHolder.SkewToleranceSeconds > 0 ? otherHolder.SkewToleranceSeconds : FileBasedLockManager.DefaultSkewToleranceSeconds;
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
                        ownExisting.data.ExpiresAtUtc = now.AddSeconds(lockReq.TimeoutSeconds);
                        ownExisting.data.DurationSeconds = lockReq.TimeoutSeconds;
                        byte[] extBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ownExisting.data, new JsonSerializerOptions { WriteIndented = true }));
                        using var extStream = new MemoryStream(extBytes);
                        await DriveUpdateFileContentAsync(ownExisting.driveItem.Id, extStream, ct);
                        FileBasedLockManager.SaveLocalLockSnapshot(remoteProjectDir, ownExisting.data);

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

                    // 2. Crea un file claim univoco (POST) che non sovrascrive mai il lock altrui
                    string lockToken = Guid.NewGuid().ToString("N");
                    var newLock = new FileLockData
                    {
                        ProjectId = lockReq.ProjectId,
                        User = lockReq.User,
                        Machine = lockReq.Machine,
                        LockToken = lockToken,
                        AcquiredAtUtc = now,
                        ExpiresAtUtc = now.AddSeconds(lockReq.TimeoutSeconds),
                        DurationSeconds = lockReq.TimeoutSeconds,
                        SkewToleranceSeconds = FileBasedLockManager.DefaultSkewToleranceSeconds
                    };

                    string claimFileName = $"sync_lock_{lockToken}.json";
                    byte[] claimBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(newLock, new JsonSerializerOptions { WriteIndented = true }));
                    DriveFileItem? createdClaim;
                    using (var claimStream = new MemoryStream(claimBytes))
                    {
                        createdClaim = await DriveCreateAndUploadFileAsync(claimFileName, cloudConnectorId, claimStream, ct);
                    }

                    // 3. Attesa anti-collisione e verifica deterministica su Google Drive
                    await Task.Delay(800, ct);

                    var postLocks = await ReadCloudLocksAsync(cloudConnectorId, NetworkTimeHelper.GetAccurateUtc(), lockReq.TimeoutSeconds, deleteExpired: false, ct);
                    var winner = postLocks
                        .OrderBy(x => string.Equals(x.driveItem.Name, "sync_lock.json", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(x => x.driveItem.CreatedTime ?? x.driveItem.ModifiedTime ?? DateTimeOffset.MaxValue)
                        .ThenBy(x => x.driveItem.Id, StringComparer.Ordinal)
                        .FirstOrDefault();

                    if (winner.data == null || !string.Equals(winner.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase))
                    {
                        if (createdClaim != null && !string.IsNullOrEmpty(createdClaim.Id))
                        {
                            try { await DriveDeleteFileAsync(createdClaim.Id, ct); } catch { }
                        }
                        else
                        {
                            var ownCreated = postLocks.FirstOrDefault(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
                            if (ownCreated.driveItem != null && !string.IsNullOrEmpty(ownCreated.driveItem.Id))
                            {
                                try { await DriveDeleteFileAsync(ownCreated.driveItem.Id, ct); } catch { }
                            }
                        }

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

                    FileBasedLockManager.SaveLocalLockSnapshot(remoteProjectDir, newLock);

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
                    return new LockResponse
                    {
                        Status = SyncLockStatus.Error,
                        Message = $"Errore acquisizione lock su Google Drive: {ex.Message}"
                    };
                }
            }

            // Fallback Google Drive Desktop / cartella condivisa reale
            string localCloudDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, lockReq.ProjectId);
            var fileResp = FileBasedLockManager.AcquireLock(localCloudDir, lockReq.ProjectId, lockReq.User, lockReq.Machine, lockReq.TimeoutSeconds);
            if (fileResp.Status == SyncLockStatus.LockAcquired && !string.IsNullOrEmpty(fileResp.LockToken))
            {
                await Task.Delay(500, ct);
                var verifyActive = FileBasedLockManager.GetActiveLock(localCloudDir);
                if (verifyActive != null && !string.Equals(verifyActive.LockToken, fileResp.LockToken, StringComparison.OrdinalIgnoreCase))
                {
                    return new LockResponse
                    {
                        Status = SyncLockStatus.LockDeniedBusy,
                        CurrentHolder = verifyActive.User,
                        CurrentHolderMachine = verifyActive.Machine,
                        AcquiredAtUtc = verifyActive.AcquiredAtUtc,
                        ExpiresAtUtc = verifyActive.ExpiresAtUtc,
                        Message = $"Lock conteso nella cartella condivisa: detenuto da {verifyActive.User} ({verifyActive.Machine})."
                    };
                }
            }
            return fileResp;
        }

        public async Task<bool> HeartbeatProjectLockAsync(string projectName, string lockToken, string user, int extendSeconds, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(lockToken)) return false;

            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName);
            bool localOk = FileBasedLockManager.HeartbeatLock(remoteProjectDir, lockToken, user, extendSeconds);

            if (HasAuth)
            {
                try
                {
                    string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudFolderId)) return false;

                    string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudConnectorId)) return false;

                    var locks = await ReadCloudLocksAsync(cloudConnectorId, NetworkTimeHelper.GetAccurateUtc(), extendSeconds, deleteExpired: false, ct);
                    var mine = locks.FirstOrDefault(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
                    if (mine.driveItem == null || mine.data == null)
                    {
                        return false;
                    }

                    mine.data.ExpiresAtUtc = NetworkTimeHelper.GetAccurateUtc().AddSeconds(extendSeconds);
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(mine.data, new JsonSerializerOptions { WriteIndented = true }));
                    using var ms = new MemoryStream(bytes);
                    await DriveUpdateFileContentAsync(mine.driveItem.Id, ms, ct);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return localOk;
        }

        public async Task<(bool isValid, string reason)> VerifyLockOwnershipAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(lockToken))
            {
                return (false, "Token di lock mancante. Caricamento bloccato per evitare sovrascritture.");
            }

            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            if (!HasAuth && ShouldRequireGoogleAuth(projectName))
            {
                return (false, "Account Google non connesso o sessione scaduta.");
            }

            if (HasAuth)
            {
                string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId))
                {
                    return (false, $"Cartella progetto '{projectName}' non trovata su Google Drive.");
                }

                string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudConnectorId))
                {
                    return (false, "Il lock di sincronizzazione è stato rimosso o non è più presente su Google Drive.");
                }

                var now = NetworkTimeHelper.GetAccurateUtc();
                var activeLocks = await ReadCloudLocksAsync(cloudConnectorId, now, _config.SyncTimeoutSeconds, deleteExpired: false, ct);

                var otherActive = activeLocks.FirstOrDefault(x =>
                    !string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
                if (otherActive.data != null)
                {
                    return (false, $"Il lock di sincronizzazione appartiene ora a '{otherActive.data.User}' ({otherActive.data.Machine}). Caricamento bloccato.");
                }

                bool stillMine = activeLocks.Any(x => string.Equals(x.data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase));
                if (!stillMine)
                {
                    return (false, "Il tuo lock di sincronizzazione è scaduto o è stato forzatamente sbloccato prima dell'upload. Ripeti la sincronizzazione.");
                }

                return (true, string.Empty);
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName);
            var activeLocal = FileBasedLockManager.GetActiveLock(remoteProjectDir);
            if (activeLocal == null || !string.Equals(activeLocal.LockToken, lockToken, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Il lock di sincronizzazione nella cartella condivisa non è più valido o appartiene a un altro utente.");
            }

            return (true, string.Empty);
        }

        public async Task<bool> ReleaseProjectLockAsync(string projectName, string lockToken, string user, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName);
            bool localReleased = FileBasedLockManager.ReleaseLock(remoteProjectDir, lockToken, user);

            if (HasAuth)
            {
                try
                {
                    string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudFolderId)) return localReleased;

                    string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudConnectorId)) return true;

                    var items = await DriveListFilesAsync(
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
                            try { await DriveDeleteFileAsync(item.Id, ct); } catch { }
                            continue;
                        }

                        try
                        {
                            using var ms = new MemoryStream();
                            await DriveDownloadFileAsync(item.Id, ms, ct);
                            var data = JsonSerializer.Deserialize<FileLockData>(Encoding.UTF8.GetString(ms.ToArray()));
                            int tolerance = data?.SkewToleranceSeconds > 0 ? data.SkewToleranceSeconds : FileBasedLockManager.DefaultSkewToleranceSeconds;
                            bool matchesToken = !string.IsNullOrWhiteSpace(lockToken) &&
                                                data != null &&
                                                string.Equals(data.LockToken, lockToken, StringComparison.OrdinalIgnoreCase);
                            bool matchesUserNoToken = string.IsNullOrWhiteSpace(lockToken) &&
                                                      data != null &&
                                                      data.User.Equals(user, StringComparison.OrdinalIgnoreCase);
                            bool isExpired = data == null || data.ExpiresAtUtc.AddSeconds(tolerance) <= now;

                            if (matchesToken || matchesUserNoToken || isExpired)
                            {
                                await DriveDeleteFileAsync(item.Id, ct);
                            }
                        }
                        catch { }
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return localReleased;
        }

        public async Task<bool> ForceBreakProjectLockAsync(string projectName, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName);
            bool localOk = FileBasedLockManager.ForceBreakLock(remoteProjectDir);

            if (HasAuth)
            {
                try
                {
                    string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudFolderId)) return localOk;

                    string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                    if (string.IsNullOrEmpty(cloudConnectorId)) return true;

                    var items = await DriveListFilesAsync(
                        $"'{cloudConnectorId}' in parents and trashed = false",
                        "id,name,mimeType",
                        100,
                        ct);

                    foreach (var item in items.Where(f => f.MimeType != "application/vnd.google-apps.folder" && IsSyncLockFileName(f.Name)))
                    {
                        await DriveDeleteFileAsync(item.Id, ct);
                    }
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return localOk;
        }

        public async Task<string> ResolveAndSyncConnectorDirAsync(string projectName, bool pullFromCloud, CancellationToken ct = default)
        {
            ReloadConfig();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            string? cloudFolderId = HasAuth ? await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct) : null;
            string remoteProjectDir = DriveShortcutResolver.ResolveProjectDirectory(_config.CloudStoragePath, projectName, cloudFolderId);
            string connectorDir = Path.Combine(remoteProjectDir, ".connector");
            Directory.CreateDirectory(connectorDir);

            if (pullFromCloud && HasAuth && !string.IsNullOrEmpty(cloudFolderId))
            {
                try
                {
                    string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: false, ct);
                    if (!string.IsNullOrEmpty(cloudConnectorId))
                    {
                        await DownloadCloudFolderRecursivelyAsync(cloudConnectorId, connectorDir, isInsideCentral: true, ct);
                    }
                }
                catch { }
            }

            return remoteProjectDir;
        }

        public async Task PushConnectorStateToCloudAsync(string projectName, string remoteProjectDir, CancellationToken ct = default)
        {
            if (!HasAuth) return;
            try
            {
                string? cloudFolderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
                if (string.IsNullOrEmpty(cloudFolderId)) return;

                string connectorDir = Path.Combine(remoteProjectDir, ".connector");
                if (!Directory.Exists(connectorDir)) return;

                string? cloudConnectorId = await GetSubfolderIdAsync(cloudFolderId, ".connector", createIfMissing: true, ct);
                if (!string.IsNullOrEmpty(cloudConnectorId))
                {
                    await UploadCloudFolderRecursivelyAsync(connectorDir, cloudConnectorId, isInsideCentral: true, ct, deleteMissingOverride: false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Avviso PushConnectorStateToCloudAsync: {ex.Message}");
            }
        }

        public async Task UpdateActiveUserSessionAsync(string projectName, string user, string machine, bool isActive, bool isSyncing = false, CancellationToken ct = default)
        {
            try
            {
                string remoteProjectDir = await ResolveAndSyncConnectorDirAsync(projectName, pullFromCloud: true, ct);
                string connectorDir = Path.Combine(remoteProjectDir, ".connector");
                Directory.CreateDirectory(connectorDir);
                string sessionsFile = Path.Combine(connectorDir, "active_users.json");

                var sessions = new List<UserSession>();
                if (File.Exists(sessionsFile))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(sessionsFile, ct);
                        sessions = JsonSerializer.Deserialize<List<UserSession>>(json) ?? new List<UserSession>();
                    }
                    catch { }
                }

                var now = NetworkTimeHelper.GetAccurateUtc();
                sessions.RemoveAll(s => (now - s.LastHeartbeatUtc).TotalHours > 4 ||
                    (s.UserName.Equals(user, StringComparison.OrdinalIgnoreCase) && s.MachineName.Equals(machine, StringComparison.OrdinalIgnoreCase)));

                if (isActive && !string.IsNullOrWhiteSpace(user))
                {
                    sessions.Add(new UserSession
                    {
                        UserName = user,
                        MachineName = machine,
                        LastHeartbeatUtc = now,
                        IsSyncing = isSyncing
                    });
                }

                await File.WriteAllTextAsync(sessionsFile, JsonSerializer.Serialize(sessions, new JsonSerializerOptions { WriteIndented = true }), ct);
                await PushConnectorStateToCloudAsync(projectName, remoteProjectDir, ct);
            }
            catch { }
        }

        public async Task<List<UserSession>> GetActiveUsersAsync(string projectName, CancellationToken ct = default)
        {
            var sessions = new List<UserSession>();
            if (string.IsNullOrWhiteSpace(projectName)) return sessions;

            try
            {
                string remoteProjectDir = await ResolveAndSyncConnectorDirAsync(projectName, pullFromCloud: true, ct);
                string sessionsFile = Path.Combine(remoteProjectDir, ".connector", "active_users.json");
                if (File.Exists(sessionsFile))
                {
                    var json = await File.ReadAllTextAsync(sessionsFile, ct);
                    var loaded = JsonSerializer.Deserialize<List<UserSession>>(json);
                    if (loaded != null)
                    {
                        var now = NetworkTimeHelper.GetAccurateUtc();
                        sessions = loaded.Where(s => (now - s.LastHeartbeatUtc).TotalHours <= 4).ToList();
                    }
                }
            }
            catch { }

            return sessions;
        }

        private async Task<string?> GetSubfolderIdAsync(string parentFolderId, string subFolderName, bool createIfMissing, CancellationToken ct)
        {
            if (!HasAuth) return null;

            string query = $"'{parentFolderId}' in parents and name = '{subFolderName.Replace("'", "\\'")}' and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            var files = await DriveListFilesAsync(query, "id,name,createdTime,modifiedTime", 20, ct);

            if (files.Count > 0)
            {
                return files
                    .OrderBy(f => f.CreatedTime ?? f.ModifiedTime ?? DateTimeOffset.MaxValue)
                    .ThenBy(f => f.Id, StringComparer.Ordinal)
                    .First()
                    .Id;
            }

            if (!createIfMissing) return null;

            var created = await DriveCreateMetadataOnlyAsync(new
            {
                name = subFolderName,
                mimeType = "application/vnd.google-apps.folder",
                parents = new[] { parentFolderId }
            }, ct);

            // Verifica anti-duplicato se due client creano la sottocartella contemporaneamente
            await Task.Delay(250, ct);
            var postFiles = await DriveListFilesAsync(query, "id,name,createdTime,modifiedTime", 20, ct);
            if (postFiles.Count > 1)
            {
                var canonical = postFiles
                    .OrderBy(f => f.CreatedTime ?? f.ModifiedTime ?? DateTimeOffset.MaxValue)
                    .ThenBy(f => f.Id, StringComparer.Ordinal)
                    .First();

                if (!string.Equals(canonical.Id, created.Id, StringComparison.Ordinal))
                {
                    try { await DriveDeleteFileAsync(created.Id, ct); } catch { }
                    return canonical.Id;
                }
            }

            return created.Id;
        }

        public async Task<string?> GetProjectFolderIdAsync(string projectName, bool createIfMissing = true, CancellationToken ct = default)
        {
            if (!HasAuth || string.IsNullOrWhiteSpace(projectName)) return null;

            string localManifest = Path.Combine(_config.LocalCachePath, projectName, "manifest.json");
            if (File.Exists(localManifest))
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(localManifest, ct);
                    var m = JsonSerializer.Deserialize<ProjectManifest>(mJson);
                    if (!string.IsNullOrEmpty(m?.CloudFolderId)) return m.CloudFolderId;
                }
                catch { }
            }

            try
            {
                string escapedName = projectName.Replace("'", "\\'");
                var files = await DriveListFilesAsync(
                    $"name = '{escapedName}' and (mimeType = 'application/vnd.google-apps.folder' or mimeType = 'application/vnd.google-apps.shortcut') and trashed = false",
                    "id,name,mimeType,shortcutDetails,parents,shared,owners",
                    10,
                    ct);

                if (files.Count > 0)
                {
                    foreach (var f in files)
                    {
                        if (f.MimeType == "application/vnd.google-apps.shortcut" && !string.IsNullOrEmpty(f.ShortcutDetails?.TargetId))
                        {
                            return f.ShortcutDetails.TargetId;
                        }
                    }
                    var target = files.FirstOrDefault(f => f.Shared == true) ?? files[0];
                    return target.Id;
                }

                return createIfMissing ? await EnsureCloudProjectFolderExistsAsync(projectName, ct) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Errore ricerca cartella '{projectName}': {ex.Message}");
                return null;
            }
        }

        public async Task<string?> EnsureCloudProjectFolderExistsAsync(string projectName, CancellationToken ct = default)
        {
            if (!HasAuth) return null;

            try
            {
                string parentId = "root";
                var parentFiles = await DriveListFilesAsync(
                    "name = 'RevitProjects' and mimeType = 'application/vnd.google-apps.folder' and trashed = false",
                    "id,name",
                    10,
                    ct);

                if (parentFiles.Count > 0)
                {
                    parentId = parentFiles[0].Id;
                }
                else
                {
                    var createdParent = await DriveCreateMetadataOnlyAsync(new
                    {
                        name = "RevitProjects",
                        mimeType = "application/vnd.google-apps.folder"
                    }, ct);
                    parentId = createdParent.Id;
                }

                var createdFolder = await DriveCreateMetadataOnlyAsync(new
                {
                    name = projectName,
                    mimeType = "application/vnd.google-apps.folder",
                    parents = new[] { parentId }
                }, ct);
                return createdFolder.Id;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Creazione cartella cloud '{projectName}': {ex.Message}");
                return null;
            }
        }

        public async Task<CollaboratorOperationResult> AddCollaboratorAsync(string projectName, string email, string role = "writer", bool sendNotification = true, CancellationToken ct = default)
        {
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            if (!HasAuth)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = "Account Google non collegato nel plugin. Accedi con Google da 'Account Google'."
                };
            }

            string? folderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId))
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Impossibile localizzare la cartella del progetto '{projectName}' su Google Drive."
                };
            }

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions?sendNotificationEmail={sendNotification.ToString().ToLowerInvariant()}&supportsAllDrives=true&fields=id,type,role,emailAddress,displayName,photoLink";
                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Post, url, ct);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    type = "user",
                    role = role,
                    emailAddress = email
                }), Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                var created = JsonSerializer.Deserialize<DrivePermissionItem>(json) ?? new DrivePermissionItem();

                return new CollaboratorOperationResult
                {
                    Success = true,
                    Message = $"Progetto condiviso con {email} con successo!",
                    Collaborator = new CollaboratorItem
                    {
                        Id = created.Id,
                        Email = created.EmailAddress ?? email,
                        DisplayName = !string.IsNullOrEmpty(created.DisplayName) ? created.DisplayName : email,
                        Role = created.Role ?? role,
                        PhotoLink = created.PhotoLink
                    }
                };
            }
            catch (Exception ex)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Errore Google Drive durante l'aggiunta di {email}: {ex.Message}"
                };
            }
        }

        public async Task<List<CollaboratorItem>> ListCollaboratorsAsync(string projectName, CancellationToken ct = default)
        {
            var list = new List<CollaboratorItem>();
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }
            if (!HasAuth) return list;

            string? folderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId)) return list;

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions?supportsAllDrives=true&fields=permissions(id,type,role,emailAddress,displayName,photoLink)";
                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, url, ct);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                var res = JsonSerializer.Deserialize<DrivePermissionListResponse>(json);

                if (res?.Permissions != null)
                {
                    foreach (var p in res.Permissions)
                    {
                        list.Add(new CollaboratorItem
                        {
                            Id = p.Id,
                            Email = p.EmailAddress ?? "Chiunque abbia il link",
                            DisplayName = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : (p.EmailAddress ?? p.Id),
                            Role = p.Role ?? "reader",
                            PhotoLink = p.PhotoLink
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoogleDriveService] Errore elenco collaboratori '{projectName}': {ex.Message}");
            }

            return list;
        }

        public async Task<CollaboratorOperationResult> RemoveCollaboratorAsync(string projectName, string permissionId, CancellationToken ct = default)
        {
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }
            if (!HasAuth)
            {
                return new CollaboratorOperationResult { Success = false, Message = "Account Google non collegato." };
            }

            string? folderId = await GetProjectFolderIdAsync(projectName, createIfMissing: false, ct);
            if (string.IsNullOrEmpty(folderId))
            {
                return new CollaboratorOperationResult { Success = false, Message = "Cartella progetto non trovata." };
            }

            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(folderId)}/permissions/{Uri.EscapeDataString(permissionId)}?supportsAllDrives=true";
                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Delete, url, ct);
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();

                return new CollaboratorOperationResult
                {
                    Success = true,
                    Message = "Collaboratore rimosso con successo."
                };
            }
            catch (Exception ex)
            {
                return new CollaboratorOperationResult
                {
                    Success = false,
                    Message = $"Errore rimozione: {ex.Message}"
                };
            }
        }

        private async Task SyncDirectoryAsync(string sourceDir, string targetDir, bool cleanRemovedFiles, CancellationToken ct)
        {
            if (!Directory.Exists(sourceDir)) return;
            Directory.CreateDirectory(targetDir);

            var sourceRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sourceDir, file);
                string fileName = Path.GetFileName(file);
                if (ContainsPathSegment(rel, "Local") || ContainsPathSegment(rel, "Revit_temp") || IsTransientTempFile(fileName))
                {
                    continue;
                }

                sourceRelPaths.Add(rel);

                string targetFile = Path.Combine(targetDir, rel);
                string? parent = Path.GetDirectoryName(targetFile);
                if (parent != null) Directory.CreateDirectory(parent);

                bool needCopy = true;
                if (File.Exists(targetFile))
                {
                    var srcInfo = new FileInfo(file);
                    var tgtInfo = new FileInfo(targetFile);
                    if (srcInfo.Length == tgtInfo.Length)
                    {
                        string hSrc = await FileHashHelper.ComputeSha256Async(file, ct);
                        string hTgt = await FileHashHelper.ComputeSha256Async(targetFile, ct);
                        if (hSrc == hTgt) needCopy = false;
                    }
                }

                if (needCopy)
                {
                    File.Copy(file, targetFile, true);
                }
            }

            if (cleanRemovedFiles && Directory.Exists(targetDir))
            {
                foreach (var tgtFile in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(targetDir, tgtFile);
                    string fileName = Path.GetFileName(tgtFile);
                    if (ContainsPathSegment(rel, "Revit_temp") || fileName.EndsWith(".slog", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!sourceRelPaths.Contains(rel))
                    {
                        try { File.Delete(tgtFile); } catch { }
                    }
                }
            }
        }
    }
}
