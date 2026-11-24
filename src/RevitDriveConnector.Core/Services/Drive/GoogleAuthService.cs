using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Util.Store;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services.Drive
{
    /// <summary>
    /// Handles Google OAuth 2.0 credentials, token_store lifecycle, access token refresh, and pre-flight validation.
    /// </summary>
    public class GoogleAuthService
    {
        private static readonly string[] Scopes =
        {
            "https://www.googleapis.com/auth/drive",
            "https://www.googleapis.com/auth/drive.file"
        };

        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task<string>>? _customTokenProvider;
        private readonly bool _useFixedConfig;
        private UserCredential? _credential;
        private string _connectedEmail = string.Empty;

        public string AppDataDir { get; }
        public string CredentialsPath => Path.Combine(AppDataDir, "credentials.json");
        public string TokenStorePath => Path.Combine(AppDataDir, "token_store");

        public bool HasAuth => _credential != null || _customTokenProvider != null;
        public string ConnectedEmail => _connectedEmail;

        public event Action? OnAccountDisconnected;

        public GoogleAuthService(
            HttpClient httpClient,
            bool useFixedConfig = false,
            Func<CancellationToken, Task<string>>? customTokenProvider = null,
            string? customAppDataDir = null)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _useFixedConfig = useFixedConfig;
            _customTokenProvider = customTokenProvider;
            AppDataDir = string.IsNullOrWhiteSpace(customAppDataDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector")
                : customAppDataDir;
            Directory.CreateDirectory(AppDataDir);
        }

        public void EnsureCredentialsExist()
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
                        AppLogger.Info("GoogleAuthService", $"Copiato credentials.json da '{candidate}'.");
                        break;
                    }
                    catch { }
                }
            }
        }

        public void InvalidateCredentialAndClearTokenStore()
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
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleAuthService", "Impossibile eliminare token_store", ex);
            }
            OnAccountDisconnected?.Invoke();
        }

        public static bool IsAuthenticationError(Exception ex)
        {
            if (ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException tre)
            {
                string err = tre.Error?.Error ?? "";
                return string.Equals(err, "invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(err, "invalid_client", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(err, "unauthorized_client", StringComparison.OrdinalIgnoreCase) ||
                       tre.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
            }
            if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Unauthorized)
            {
                return true;
            }
            return ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                   ex.Message.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (!HasAuth)
            {
                throw new InvalidOperationException("Account Google non autenticato. Apri 'Account Google' nel Ribbon e accedi.");
            }

            try
            {
                return _customTokenProvider != null
                    ? await _customTokenProvider(ct)
                    : await _credential!.GetAccessTokenForRequestAsync(null, ct);
            }
            catch (Exception ex) when (IsAuthenticationError(ex))
            {
                AppLogger.Warn("GoogleAuthService", "Token OAuth scaduto o revocato; pulizia token_store.", ex);
                InvalidateCredentialAndClearTokenStore();
                throw;
            }
        }

        public async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(HttpMethod method, string url, CancellationToken ct)
        {
            string token = await GetAccessTokenAsync(ct);
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        }

        public async Task<GoogleAccountInfo?> ValidateCurrentCredentialAsync(CancellationToken ct = default)
        {
            if (!HasAuth) return null;

            try
            {
                using var req = await CreateAuthorizedRequestAsync(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user", ct);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    AppLogger.Warn("GoogleAuthService", "Risposta 401 Unauthorized da Google Drive API. Pulizia token_store.");
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
                AppLogger.Warn("GoogleAuthService", "Verifica stato utente fallita", ex);
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
                        AppLogger.Warn("GoogleAuthService", "Sessione Google salvata non valida o scaduta.");
                        return false;
                    }

                    AppLogger.Info("GoogleAuthService", $"Connessione automatica Google Drive API v3 riuscita ({_connectedEmail}).");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleAuthService", "Inizializzazione OAuth fallita", ex);
                if (IsAuthenticationError(ex))
                {
                    InvalidateCredentialAndClearTokenStore();
                }
                return false;
            }
        }

        /// <summary>
        /// Ensures an active, valid OAuth token before starting a critical sync/upload operation.
        /// </summary>
        public async Task EnsureConnectedAsync(bool allowInteractiveLogin = false, CancellationToken ct = default)
        {
            if (!HasAuth)
            {
                await InitializeAsync(ct);
            }

            if (!HasAuth && allowInteractiveLogin)
            {
                var acct = await ConnectAccountAsync(ct);
                if (!acct.IsConnected)
                {
                    throw new InvalidOperationException(
                        $"Per usare Google Drive Connector è necessario collegare il tuo Account Google.\nDettaglio: {acct.StatusMessage}");
                }
            }

            if (!HasAuth)
            {
                throw new InvalidOperationException(
                    "Account Google non collegato o sessione scaduta. Apri 'Account Google' nel Ribbon e accedi prima di procedere.");
            }

            // Force refresh check so expired access tokens are renewed before Revit commits local changes
            await GetAccessTokenAsync(ct);
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
                AppLogger.Warn("GoogleAuthService", "Errore connessione OAuth, tento ri-autenticazione pulita", ex);
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
                    AppLogger.Error("GoogleAuthService", "Ri-autenticazione OAuth fallita", retryEx);
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
            InvalidateCredentialAndClearTokenStore();
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
                AppLogger.Error("GoogleAuthService", "Errore salvataggio credenziali", ex);
                return false;
            }
        }
    }
}
