using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services.Drive
{
    /// <summary>
    /// Low-level Google Drive API v3 REST client with:
    /// - Exponential backoff + jitter retry on ALL HTTP operations (GET/POST/PATCH/DELETE/PUT)
    /// - Full Google Workspace Shared Drives support (corpora=allDrives, supportsAllDrives, trash fallback on 403)
    /// - True chunked resumable uploads (8 MB default chunks, 308 Resume Incomplete & byte-range offset recovery)
    /// - Resumable Range-based streaming downloads for large (1 GB+) Revit files
    /// </summary>
    internal class DriveApiClient
    {
        private const int ChunkGranularityBytes = 256 * 1024; // Google Drive requires chunk sizes to be multiples of 256 KB
        private readonly HttpClient _http;
        private readonly GoogleAuthService _auth;
        private readonly Func<int> _chunkSizeBytesProvider;

        public DriveApiClient(HttpClient httpClient, GoogleAuthService authService, Func<int>? chunkSizeBytesProvider = null)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _auth = authService ?? throw new ArgumentNullException(nameof(authService));
            _chunkSizeBytesProvider = chunkSizeBytesProvider ?? (() => 8 * 1024 * 1024);
        }

        public static bool IsTransientStatusCode(HttpStatusCode code)
        {
            int status = (int)code;
            return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
        }

        private int GetAlignedChunkSize()
        {
            int requested = _chunkSizeBytesProvider();
            if (requested < ChunkGranularityBytes) requested = ChunkGranularityBytes;
            int aligned = (requested / ChunkGranularityBytes) * ChunkGranularityBytes;
            return Math.Max(ChunkGranularityBytes, aligned);
        }

        private static int ComputeBackoffMs(int attempt, HttpResponseMessage? resp)
        {
            if (resp?.Headers.RetryAfter?.Delta is TimeSpan delta && delta.TotalMilliseconds > 0 && delta.TotalSeconds <= 30)
            {
                return (int)delta.TotalMilliseconds;
            }

            int baseDelay = attempt switch
            {
                0 => 400,
                1 => 1000,
                2 => 2200,
                3 => 4500,
                _ => 8000
            };

            int jitter = Random.Shared.Next(0, 250);
            return baseDelay + jitter;
        }

        public async Task<HttpResponseMessage> SendAuthorizedWithRetryAsync(
            Func<CancellationToken, Task<HttpRequestMessage>> requestFactory,
            HttpCompletionOption completionOption,
            CancellationToken ct,
            int maxRetries = 4)
        {
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                using var req = await requestFactory(ct);
                HttpResponseMessage? resp = null;
                try
                {
                    resp = await _http.SendAsync(req, completionOption, ct);
                    if (resp.IsSuccessStatusCode || !IsTransientStatusCode(resp.StatusCode) || attempt == maxRetries)
                    {
                        return resp;
                    }

                    int waitMs = ComputeBackoffMs(attempt, resp);
                    AppLogger.Warn("DriveApiClient", $"HTTP {(int)resp.StatusCode} su {req.Method} {req.RequestUri?.AbsolutePath}; retry {attempt + 1}/{maxRetries} tra {waitMs}ms.");
                    resp.Dispose();
                    await Task.Delay(waitMs, ct);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    resp?.Dispose();
                    int waitMs = ComputeBackoffMs(attempt, null);
                    AppLogger.Warn("DriveApiClient", $"Errore rete su {req.Method} {req.RequestUri?.AbsolutePath}; retry {attempt + 1}/{maxRetries} tra {waitMs}ms.", ex);
                    await Task.Delay(waitMs, ct);
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < maxRetries)
                {
                    resp?.Dispose();
                    int waitMs = ComputeBackoffMs(attempt, null);
                    AppLogger.Warn("DriveApiClient", $"Timeout HTTP transitorio su {req.Method} {req.RequestUri?.AbsolutePath}; retry {attempt + 1}/{maxRetries} tra {waitMs}ms.", ex);
                    await Task.Delay(waitMs, ct);
                }
            }

            throw new HttpRequestException("Richiesta HTTP verso Google Drive fallita dopo tutti i tentativi di retry.");
        }

        public async Task<List<DriveFileItem>> ListFilesAsync(string query, string fields, int pageSize = 100, CancellationToken ct = default)
        {
            var allFiles = new List<DriveFileItem>();
            string? pageToken = null;
            bool fetchAllPages = pageSize >= 100;
            int maxPages = fetchAllPages ? 25 : 1;

            for (int page = 0; page < maxPages; page++)
            {
                string url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString($"nextPageToken,files({fields})")}&pageSize={pageSize}&corpora=allDrives&supportsAllDrives=true&includeItemsFromAllDrives=true";
                if (!string.IsNullOrEmpty(pageToken))
                {
                    url += $"&pageToken={Uri.EscapeDataString(pageToken)}";
                }

                using var resp = await SendAuthorizedWithRetryAsync(
                    token => _auth.CreateAuthorizedRequestAsync(HttpMethod.Get, url, token),
                    HttpCompletionOption.ResponseContentRead,
                    ct);

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

        public async Task<DriveFileItem?> GetFileMetadataAsync(string fileId, string fields, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(fields)}&supportsAllDrives=true";
            using var resp = await SendAuthorizedWithRetryAsync(
                token => _auth.CreateAuthorizedRequestAsync(HttpMethod.Get, url, token),
                HttpCompletionOption.ResponseContentRead,
                ct);

            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DriveFileItem>(json);
        }

        /// <summary>
        /// Downloads a file into <paramref name="destination"/> with Range-based resume if a stream drops mid-download.
        /// </summary>
        public async Task DownloadFileAsync(
            string fileId,
            Stream destination,
            CancellationToken ct = default,
            Action<long>? onBytesDelta = null)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media&supportsAllDrives=true";

            if (destination.CanSeek)
            {
                destination.SetLength(0);
                destination.Position = 0;
            }

            long totalReceived = 0;
            const int maxResumeAttempts = 5;

            for (int attempt = 0; attempt <= maxResumeAttempts; attempt++)
            {
                using var resp = await SendAuthorizedWithRetryAsync(async token =>
                {
                    var req = await _auth.CreateAuthorizedRequestAsync(HttpMethod.Get, url, token);
                    if (totalReceived > 0 && destination.CanSeek)
                    {
                        req.Headers.Range = new RangeHeaderValue(totalReceived, null);
                    }
                    return req;
                }, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && totalReceived > 0)
                {
                    // File already completely downloaded
                    return;
                }

                resp.EnsureSuccessStatusCode();

                // If server ignored Range and returned 200 OK instead of 206 PartialContent, restart cleanly
                if (totalReceived > 0 && resp.StatusCode == HttpStatusCode.OK && destination.CanSeek)
                {
                    destination.SetLength(0);
                    destination.Position = 0;
                    onBytesDelta?.Invoke(-totalReceived);
                    totalReceived = 0;
                }

                try
                {
                    using var contentStream = await resp.Content.ReadAsStreamAsync(ct);
                    byte[] buffer = new byte[256 * 1024];
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        totalReceived += bytesRead;
                        onBytesDelta?.Invoke(bytesRead);
                    }

                    return;
                }
                catch (Exception ex) when ((ex is IOException || ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) &&
                                           attempt < maxResumeAttempts && destination.CanSeek)
                {
                    int waitMs = ComputeBackoffMs(attempt, null);
                    AppLogger.Warn("DriveApiClient", $"Download interrotto a {totalReceived} byte per file {fileId}; ripresa Range tra {waitMs}ms.", ex);
                    await Task.Delay(waitMs, ct);
                }
            }
        }

        public async Task<DriveFileItem> CreateMetadataOnlyAsync(object bodyObj, CancellationToken ct = default)
        {
            string url = "https://www.googleapis.com/drive/v3/files?supportsAllDrives=true&fields=id,name,mimeType,createdTime,modifiedTime,driveId";
            string bodyJson = JsonSerializer.Serialize(bodyObj);

            using var resp = await SendAuthorizedWithRetryAsync(async token =>
            {
                var req = await _auth.CreateAuthorizedRequestAsync(HttpMethod.Post, url, token);
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                return req;
            }, HttpCompletionOption.ResponseContentRead, ct);

            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<DriveFileItem>(json) ?? new DriveFileItem();
        }

        public Task<DriveFileItem?> CreateAndUploadFileAsync(
            string fileName,
            string parentFolderId,
            Stream fileStream,
            CancellationToken ct = default,
            Action<long>? onBytesDelta = null)
        {
            string initUrl = "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&supportsAllDrives=true&fields=id,name,createdTime,modifiedTime,md5Checksum,size";
            string metaJson = JsonSerializer.Serialize(new
            {
                name = fileName,
                parents = new[] { parentFolderId }
            });
            return UploadResumableCoreAsync(HttpMethod.Post, initUrl, metaJson, fileStream, ct, onBytesDelta);
        }

        public Task<DriveFileItem?> UpdateFileContentAsync(
            string fileId,
            Stream fileStream,
            CancellationToken ct = default,
            Action<long>? onBytesDelta = null)
        {
            string initUrl = $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=resumable&supportsAllDrives=true&fields=id,name,createdTime,modifiedTime,md5Checksum,size";
            return UploadResumableCoreAsync(HttpMethod.Patch, initUrl, "{}", fileStream, ct, onBytesDelta);
        }

        /// <summary>
        /// True Google Drive v3 resumable chunked upload with 308 Resume Incomplete handling
        /// and automatic server-offset query recovery after transient network or 5xx/429 failures.
        /// </summary>
        private async Task<DriveFileItem?> UploadResumableCoreAsync(
            HttpMethod initMethod,
            string initUrl,
            string metadataJson,
            Stream fileStream,
            CancellationToken ct,
            Action<long>? onBytesDelta)
        {
            if (!fileStream.CanSeek)
            {
                throw new InvalidOperationException("Lo stream di upload deve supportare Seek per l'upload resumable a chunk.");
            }

            long totalLength = fileStream.Length;

            // 1. Initiate resumable session with retry
            Uri? uploadUri;
            using (var initResp = await SendAuthorizedWithRetryAsync(async token =>
            {
                var initReq = await _auth.CreateAuthorizedRequestAsync(initMethod, initUrl, token);
                initReq.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
                initReq.Headers.Add("X-Upload-Content-Length", totalLength.ToString());
                initReq.Content = new StringContent(metadataJson, Encoding.UTF8, "application/json");
                return initReq;
            }, HttpCompletionOption.ResponseContentRead, ct))
            {
                initResp.EnsureSuccessStatusCode();
                uploadUri = initResp.Headers.Location;
            }

            if (uploadUri == null)
            {
                throw new InvalidOperationException("Google Drive non ha restituito l'URL della sessione di upload resumable.");
            }

            // Special case: 0-byte empty file
            if (totalLength == 0)
            {
                using var emptyResp = await SendAuthorizedWithRetryAsync(async token =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                    req.Content = new ByteArrayContent(Array.Empty<byte>());
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return await Task.FromResult(req);
                }, HttpCompletionOption.ResponseContentRead, ct);

                emptyResp.EnsureSuccessStatusCode();
                return await TryParseDriveItemAsync(emptyResp, ct);
            }

            int chunkSize = GetAlignedChunkSize();
            byte[] buffer = new byte[Math.Min(chunkSize, (int)Math.Min(totalLength, int.MaxValue))];
            long offset = 0;
            long reportedBytes = 0;
            const int maxChunkFailures = 6;
            int consecutiveFailures = 0;

            while (offset < totalLength)
            {
                ct.ThrowIfCancellationRequested();

                fileStream.Position = offset;
                int bytesToRead = (int)Math.Min(buffer.Length, totalLength - offset);
                int readTotal = 0;
                while (readTotal < bytesToRead)
                {
                    int r = await fileStream.ReadAsync(buffer.AsMemory(readTotal, bytesToRead - readTotal), ct);
                    if (r == 0) break;
                    readTotal += r;
                }

                if (readTotal == 0) break;

                long rangeStart = offset;
                long rangeEnd = offset + readTotal - 1;

                HttpResponseMessage? putResp = null;
                try
                {
                    using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                    var chunkContent = new ByteArrayContent(buffer, 0, readTotal);
                    chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    chunkContent.Headers.ContentRange = new ContentRangeHeaderValue(rangeStart, rangeEnd, totalLength);
                    putReq.Content = chunkContent;

                    putResp = await _http.SendAsync(putReq, HttpCompletionOption.ResponseContentRead, ct);
                    int statusCode = (int)putResp.StatusCode;

                    if (statusCode == 200 || statusCode == 201)
                    {
                        long delta = totalLength - reportedBytes;
                        if (delta > 0)
                        {
                            reportedBytes = totalLength;
                            onBytesDelta?.Invoke(delta);
                        }
                        var result = await TryParseDriveItemAsync(putResp, ct);
                        putResp.Dispose();
                        return result;
                    }

                    if (statusCode == 308) // Resume Incomplete -> advance to acknowledged byte
                    {
                        consecutiveFailures = 0;
                        long nextOffset = ParseNextOffsetFrom308(putResp, rangeEnd + 1);
                        putResp.Dispose();

                        if (nextOffset > reportedBytes)
                        {
                            onBytesDelta?.Invoke(nextOffset - reportedBytes);
                            reportedBytes = nextOffset;
                        }
                        offset = nextOffset;
                        continue;
                    }

                    if (IsTransientStatusCode(putResp.StatusCode) && consecutiveFailures < maxChunkFailures)
                    {
                        int waitMs = ComputeBackoffMs(consecutiveFailures, putResp);
                        consecutiveFailures++;
                        AppLogger.Warn("DriveApiClient", $"Chunk upload HTTP {statusCode} a offset {offset}/{totalLength}; verifica offset e retry {consecutiveFailures}/{maxChunkFailures} tra {waitMs}ms.");
                        putResp.Dispose();
                        await Task.Delay(waitMs, ct);

                        var (completed, serverOffset, completedItem) = await QueryUploadStatusAsync(uploadUri, totalLength, ct);
                        if (completed)
                        {
                            if (totalLength > reportedBytes) onBytesDelta?.Invoke(totalLength - reportedBytes);
                            return completedItem;
                        }
                        offset = serverOffset;
                        continue;
                    }

                    // Fatal error
                    string errBody = await putResp.Content.ReadAsStringAsync(ct);
                    var code = putResp.StatusCode;
                    putResp.Dispose();
                    throw new HttpRequestException($"Upload chunk fallito con HTTP {(int)code} ({code}): {errBody}", null, code);
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested)) &&
                                           consecutiveFailures < maxChunkFailures &&
                                           !(ex is HttpRequestException hre && hre.StatusCode.HasValue && !IsTransientStatusCode(hre.StatusCode.Value)))
                {
                    putResp?.Dispose();
                    int waitMs = ComputeBackoffMs(consecutiveFailures, null);
                    consecutiveFailures++;
                    AppLogger.Warn("DriveApiClient", $"Errore rete su chunk upload a offset {offset}/{totalLength}; ripresa {consecutiveFailures}/{maxChunkFailures} tra {waitMs}ms.", ex);
                    await Task.Delay(waitMs, ct);

                    var (completed, serverOffset, completedItem) = await QueryUploadStatusAsync(uploadUri, totalLength, ct);
                    if (completed)
                    {
                        if (totalLength > reportedBytes) onBytesDelta?.Invoke(totalLength - reportedBytes);
                        return completedItem;
                    }
                    offset = serverOffset;
                }
            }

            throw new InvalidOperationException("Upload resumable terminato senza risposta finale da Google Drive.");
        }

        private async Task<(bool Completed, long NextOffset, DriveFileItem? Item)> QueryUploadStatusAsync(
            Uri uploadUri,
            long totalLength,
            CancellationToken ct)
        {
            try
            {
                using var statusReq = new HttpRequestMessage(HttpMethod.Put, uploadUri);
                statusReq.Content = new ByteArrayContent(Array.Empty<byte>());
                statusReq.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalLength);

                using var statusResp = await _http.SendAsync(statusReq, HttpCompletionOption.ResponseContentRead, ct);
                int statusCode = (int)statusResp.StatusCode;

                if (statusCode == 200 || statusCode == 201)
                {
                    var item = await TryParseDriveItemAsync(statusResp, ct);
                    return (true, totalLength, item);
                }

                if (statusCode == 308)
                {
                    long nextOffset = ParseNextOffsetFrom308(statusResp, 0);
                    return (false, nextOffset, null);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("DriveApiClient", "Query stato upload resumable non riuscita", ex);
            }

            return (false, 0, null);
        }

        private static long ParseNextOffsetFrom308(HttpResponseMessage resp, long fallback)
        {
            if (resp.Headers.TryGetValues("Range", out var values))
            {
                foreach (var val in values)
                {
                    // Format: "bytes=0-8388607"
                    int dash = val.LastIndexOf('-');
                    if (dash >= 0 && long.TryParse(val.AsSpan(dash + 1), out long endByte))
                    {
                        return endByte + 1;
                    }
                }
            }
            return fallback;
        }

        private static async Task<DriveFileItem?> TryParseDriveItemAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                string respJson = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(respJson))
                {
                    return JsonSerializer.Deserialize<DriveFileItem>(respJson);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Deletes a file on Personal Drive, with automatic soft-trash fallback (PATCH trashed=true)
        /// for Google Workspace Shared Drives where non-Manager roles get HTTP 403 on hard DELETE.
        /// </summary>
        public async Task DeleteOrTrashFileAsync(string fileId, string? parentFolderId = null, CancellationToken ct = default)
        {
            string deleteUrl = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true";
            using var resp = await SendAuthorizedWithRetryAsync(
                token => _auth.CreateAuthorizedRequestAsync(HttpMethod.Delete, deleteUrl, token),
                HttpCompletionOption.ResponseContentRead,
                ct);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                // Shared Drives Content Manager / Contributor fallback: move to trash via PATCH
                string trashUrl = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?supportsAllDrives=true";
                using var trashResp = await SendAuthorizedWithRetryAsync(async token =>
                {
                    var req = await _auth.CreateAuthorizedRequestAsync(HttpMethod.Patch, trashUrl, token);
                    req.Content = new StringContent("{\"trashed\":true}", Encoding.UTF8, "application/json");
                    return req;
                }, HttpCompletionOption.ResponseContentRead, ct);

                if (trashResp.IsSuccessStatusCode || trashResp.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(parentFolderId))
                {
                    await RemoveParentAsync(fileId, parentFolderId, ct);
                    return;
                }

                trashResp.EnsureSuccessStatusCode();
            }

            resp.EnsureSuccessStatusCode();
        }

        public async Task RemoveParentAsync(string fileId, string parentFolderId, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?removeParents={Uri.EscapeDataString(parentFolderId)}&supportsAllDrives=true";
            using var resp = await SendAuthorizedWithRetryAsync(async token =>
            {
                var req = await _auth.CreateAuthorizedRequestAsync(HttpMethod.Patch, url, token);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                return req;
            }, HttpCompletionOption.ResponseContentRead, ct);

            if (resp.StatusCode != HttpStatusCode.NotFound)
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        public async Task<string> SendJsonRequestAsync(HttpMethod method, string url, object? bodyObj, CancellationToken ct = default)
        {
            string? bodyJson = bodyObj != null ? JsonSerializer.Serialize(bodyObj) : null;
            using var resp = await SendAuthorizedWithRetryAsync(async token =>
            {
                var req = await _auth.CreateAuthorizedRequestAsync(method, url, token);
                if (bodyJson != null)
                {
                    req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }
                return req;
            }, HttpCompletionOption.ResponseContentRead, ct);

            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }

        public async Task<List<DriveRevisionItem>> ListRevisionsAsync(string fileId, CancellationToken ct = default)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}/revisions?fields=revisions(id,mimeType,modifiedTime,size,md5Checksum,keepForever,lastModifyingUser)&supportsAllDrives=true";
            using var resp = await SendAuthorizedWithRetryAsync(
                token => _auth.CreateAuthorizedRequestAsync(HttpMethod.Get, url, token),
                HttpCompletionOption.ResponseContentRead,
                ct);

            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = JsonSerializer.Deserialize<DriveRevisionListResponse>(json);
            return parsed?.Revisions ?? new List<DriveRevisionItem>();
        }

        public async Task DownloadRevisionAsync(
            string fileId,
            string revisionId,
            Stream destination,
            CancellationToken ct = default,
            Action<long>? onBytesDelta = null)
        {
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}/revisions/{Uri.EscapeDataString(revisionId)}?alt=media&supportsAllDrives=true";

            if (destination.CanSeek)
            {
                destination.SetLength(0);
                destination.Position = 0;
            }

            using var resp = await SendAuthorizedWithRetryAsync(
                token => _auth.CreateAuthorizedRequestAsync(HttpMethod.Get, url, token),
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            resp.EnsureSuccessStatusCode();

            using var contentStream = await resp.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[256 * 1024];
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                onBytesDelta?.Invoke(bytesRead);
            }
        }
    }
}
