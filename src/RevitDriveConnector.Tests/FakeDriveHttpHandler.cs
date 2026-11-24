using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Tests
{
    /// <summary>
    /// In-memory Google Drive API v3 HTTP simulator supporting:
    /// - Metadata CRUD, files.list query filtering, Shared Drive 403->trash semantics
    /// - True chunked resumable upload sessions (308 Resume Incomplete, Range header, bytes */total status query)
    /// - Fault injection (429 / 503) to verify exponential backoff retry and mid-upload chunk recovery
    /// </summary>
    internal sealed class FakeDriveHttpHandler : HttpMessageHandler
    {
        public sealed class StoredFile
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string MimeType { get; set; } = "application/octet-stream";
            public List<string> Parents { get; set; } = new();
            public byte[] Content { get; set; } = Array.Empty<byte>();
            public bool Trashed { get; set; }
            public string? DriveId { get; set; }
            public DateTimeOffset CreatedTime { get; set; } = DateTimeOffset.UtcNow;
            public DateTimeOffset ModifiedTime { get; set; } = DateTimeOffset.UtcNow;
            public string Md5 => Convert.ToHexString(MD5.HashData(Content)).ToLowerInvariant();
        }

        private sealed class UploadSession
        {
            public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
            public string? TargetFileId { get; init; }
            public string FileName { get; init; } = string.Empty;
            public List<string> Parents { get; init; } = new();
            public long TotalLength { get; init; }
            public byte[] Buffer { get; init; } = Array.Empty<byte>();
            public long ReceivedBytes { get; set; }
        }

        private readonly object _lock = new();
        private readonly Dictionary<string, StoredFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, UploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private int _nextId = 1;

        public bool SimulateSharedDriveDeleteForbidden { get; set; }
        public int FailNextChunkPutCount { get; set; }
        public int FailNextListWith429Count { get; set; }
        public int TotalListCalls { get; private set; }
        public int TotalChunkPuts { get; private set; }
        public int TotalTrashPatches { get; private set; }

        public StoredFile AddFolder(string name, string? parentId = null, string? driveId = null)
        {
            lock (_lock)
            {
                var f = new StoredFile
                {
                    Id = $"folder_{_nextId++:D4}",
                    Name = name,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = parentId != null ? new List<string> { parentId } : new List<string> { "root" },
                    DriveId = driveId,
                    CreatedTime = DateTimeOffset.UtcNow.AddTicks(_nextId),
                    ModifiedTime = DateTimeOffset.UtcNow.AddTicks(_nextId)
                };
                _files[f.Id] = f;
                return f;
            }
        }

        public StoredFile AddFile(string name, string parentId, byte[] content)
        {
            lock (_lock)
            {
                var f = new StoredFile
                {
                    Id = $"file_{_nextId++:D4}",
                    Name = name,
                    MimeType = "application/octet-stream",
                    Parents = new List<string> { parentId },
                    Content = content.ToArray(),
                    CreatedTime = DateTimeOffset.UtcNow.AddTicks(_nextId),
                    ModifiedTime = DateTimeOffset.UtcNow.AddTicks(_nextId)
                };
                _files[f.Id] = f;
                return f;
            }
        }

        public List<StoredFile> GetAllFiles()
        {
            lock (_lock)
            {
                return _files.Values.ToList();
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? "";
            string path = request.RequestUri?.AbsolutePath ?? "";
            string query = request.RequestUri?.Query ?? "";

            // 1. Chunked resumable session PUT
            if (request.Method == HttpMethod.Put && path.StartsWith("/resumable/", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleResumablePutAsync(request, path);
            }

            // 2. Resumable upload initiation (POST or PATCH /upload/drive/v3/files...)
            if (path.StartsWith("/upload/drive/v3/files", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleUploadInitAsync(request, path);
            }

            // 3. List files (GET /drive/v3/files?q=...)
            if (request.Method == HttpMethod.Get && path.Equals("/drive/v3/files", StringComparison.OrdinalIgnoreCase))
            {
                return HandleListFiles(query);
            }

            // 4. Get file metadata or download content (GET /drive/v3/files/{id})
            if (request.Method == HttpMethod.Get && path.StartsWith("/drive/v3/files/", StringComparison.OrdinalIgnoreCase))
            {
                string fileId = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                return HandleGetOrDownloadFile(request, fileId, query);
            }

            // 5. Create metadata only (POST /drive/v3/files)
            if (request.Method == HttpMethod.Post && path.Equals("/drive/v3/files", StringComparison.OrdinalIgnoreCase))
            {
                return await HandleCreateMetadataAsync(request);
            }

            // 6. DELETE /drive/v3/files/{id}
            if (request.Method == HttpMethod.Delete && path.StartsWith("/drive/v3/files/", StringComparison.OrdinalIgnoreCase))
            {
                string fileId = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                return HandleDeleteFile(fileId);
            }

            // 7. PATCH /drive/v3/files/{id} (trash or removeParents)
            if (request.Method == HttpMethod.Patch && path.StartsWith("/drive/v3/files/", StringComparison.OrdinalIgnoreCase))
            {
                string fileId = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                return await HandlePatchFileAsync(request, fileId);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private async Task<HttpResponseMessage> HandleUploadInitAsync(HttpRequestMessage request, string path)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync() : "{}";
            long totalLength = 0;
            if (request.Headers.TryGetValues("X-Upload-Content-Length", out var vals))
            {
                long.TryParse(vals.FirstOrDefault(), out totalLength);
            }

            string? targetFileId = null;
            if (request.Method == HttpMethod.Patch && path.Length > "/upload/drive/v3/files/".Length)
            {
                targetFileId = Uri.UnescapeDataString(path["/upload/drive/v3/files/".Length..]);
            }

            string fileName = "unnamed";
            var parents = new List<string>();
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("name", out var nEl)) fileName = nEl.GetString() ?? fileName;
                if (doc.RootElement.TryGetProperty("parents", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pEl.EnumerateArray())
                    {
                        if (p.GetString() is string ps) parents.Add(ps);
                    }
                }
            }

            var session = new UploadSession
            {
                TargetFileId = targetFileId,
                FileName = fileName,
                Parents = parents,
                TotalLength = totalLength,
                Buffer = new byte[totalLength]
            };

            lock (_lock)
            {
                _sessions[session.SessionId] = session;
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.Location = new Uri($"https://www.googleapis.com/resumable/{session.SessionId}");
            return resp;
        }

        private async Task<HttpResponseMessage> HandleResumablePutAsync(HttpRequestMessage request, string path)
        {
            string sessionId = path["/resumable/".Length..];
            UploadSession? session;
            lock (_lock)
            {
                _sessions.TryGetValue(sessionId, out session);
            }
            if (session == null) return new HttpResponseMessage(HttpStatusCode.NotFound);

            byte[] chunk = request.Content != null ? await request.Content.ReadAsByteArrayAsync() : Array.Empty<byte>();
            var contentRange = request.Content?.Headers.ContentRange;

            // Status query: Content-Range: bytes */total
            if (contentRange != null && !contentRange.HasRange && contentRange.HasLength)
            {
                var statusResp = new HttpResponseMessage((HttpStatusCode)308);
                if (session.ReceivedBytes > 0)
                {
                    statusResp.Headers.TryAddWithoutValidation("Range", $"bytes=0-{session.ReceivedBytes - 1}");
                }
                return statusResp;
            }

            lock (_lock)
            {
                TotalChunkPuts++;
                if (FailNextChunkPutCount > 0 && TotalChunkPuts == 3)
                {
                    FailNextChunkPutCount--;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                long start = contentRange?.From ?? 0;
                if (chunk.Length > 0)
                {
                    Array.Copy(chunk, 0, session.Buffer, start, chunk.Length);
                    session.ReceivedBytes = Math.Max(session.ReceivedBytes, start + chunk.Length);
                }

                if (session.ReceivedBytes < session.TotalLength)
                {
                    var partial = new HttpResponseMessage((HttpStatusCode)308);
                    partial.Headers.TryAddWithoutValidation("Range", $"bytes=0-{session.ReceivedBytes - 1}");
                    return partial;
                }

                // Finalize file!
                StoredFile stored;
                if (!string.IsNullOrEmpty(session.TargetFileId) && _files.TryGetValue(session.TargetFileId, out var existing))
                {
                    existing.Content = session.Buffer.ToArray();
                    existing.ModifiedTime = DateTimeOffset.UtcNow.AddTicks(_nextId++);
                    stored = existing;
                }
                else
                {
                    stored = new StoredFile
                    {
                        Id = $"file_{_nextId++:D4}",
                        Name = session.FileName,
                        Parents = session.Parents,
                        Content = session.Buffer.ToArray(),
                        CreatedTime = DateTimeOffset.UtcNow.AddTicks(_nextId),
                        ModifiedTime = DateTimeOffset.UtcNow.AddTicks(_nextId)
                    };
                    _files[stored.Id] = stored;
                }

                _sessions.Remove(sessionId);
                return JsonResponse(SerializeFileItem(stored));
            }
        }

        private HttpResponseMessage HandleListFiles(string query)
        {
            lock (_lock)
            {
                TotalListCalls++;
                if (FailNextListWith429Count > 0)
                {
                    FailNextListWith429Count--;
                    var rateResp = new HttpResponseMessage((HttpStatusCode)429);
                    rateResp.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                    return rateResp;
                }

                string q = ExtractQueryParam(query, "q") ?? "";
                IEnumerable<StoredFile> matches = _files.Values.Where(f => !f.Trashed);

                var parentMatch = Regex.Match(q, @"'([^']+)'\s+in\s+parents");
                if (parentMatch.Success)
                {
                    string parentId = parentMatch.Groups[1].Value;
                    matches = matches.Where(f => f.Parents.Contains(parentId, StringComparer.OrdinalIgnoreCase));
                }

                if (q.Contains("mimeType = 'application/vnd.google-apps.folder'", StringComparison.OrdinalIgnoreCase) &&
                    !q.Contains("shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    matches = matches.Where(f => f.MimeType == "application/vnd.google-apps.folder");
                }

                var nameEqMatches = Regex.Matches(q, @"name\s*=\s*'([^']+)'");
                if (nameEqMatches.Count == 1)
                {
                    string name = nameEqMatches[0].Groups[1].Value;
                    matches = matches.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                }
                else if (nameEqMatches.Count > 1)
                {
                    var names = nameEqMatches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    matches = matches.Where(f => names.Contains(f.Name));
                }

                var nameContains = Regex.Match(q, @"name\s+contains\s+'([^']+)'");
                if (nameContains.Success)
                {
                    string sub = nameContains.Groups[1].Value;
                    matches = matches.Where(f => f.Name.Contains(sub, StringComparison.OrdinalIgnoreCase));
                }

                var listObj = new { files = matches.Select(SerializeFileItem).ToList() };
                return JsonResponse(listObj);
            }
        }

        private HttpResponseMessage HandleGetOrDownloadFile(HttpRequestMessage request, string fileId, string query)
        {
            lock (_lock)
            {
                if (!_files.TryGetValue(fileId, out var f) || f.Trashed)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (query.Contains("alt=media", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.Headers.Range?.Ranges.FirstOrDefault() is RangeItemHeaderValue r && r.From.HasValue)
                    {
                        long from = r.From.Value;
                        if (from >= f.Content.Length)
                        {
                            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                        }
                        byte[] slice = f.Content[(int)from..];
                        var partialResp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                        {
                            Content = new ByteArrayContent(slice)
                        };
                        return partialResp;
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(f.Content)
                    };
                }

                return JsonResponse(SerializeFileItem(f));
            }
        }

        private async Task<HttpResponseMessage> HandleCreateMetadataAsync(HttpRequestMessage request)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync() : "{}";
            using var doc = JsonDocument.Parse(body);
            string name = doc.RootElement.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "folder") : "folder";
            string mime = doc.RootElement.TryGetProperty("mimeType", out var mEl) ? (mEl.GetString() ?? "application/vnd.google-apps.folder") : "application/vnd.google-apps.folder";
            var parents = new List<string>();
            if (doc.RootElement.TryGetProperty("parents", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pEl.EnumerateArray())
                {
                    if (p.GetString() is string ps) parents.Add(ps);
                }
            }

            lock (_lock)
            {
                var f = new StoredFile
                {
                    Id = $"folder_{_nextId++:D4}",
                    Name = name,
                    MimeType = mime,
                    Parents = parents.Count > 0 ? parents : new List<string> { "root" },
                    CreatedTime = DateTimeOffset.UtcNow.AddTicks(_nextId),
                    ModifiedTime = DateTimeOffset.UtcNow.AddTicks(_nextId)
                };
                _files[f.Id] = f;
                return JsonResponse(SerializeFileItem(f));
            }
        }

        private HttpResponseMessage HandleDeleteFile(string fileId)
        {
            lock (_lock)
            {
                if (SimulateSharedDriveDeleteForbidden)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                if (_files.Remove(fileId))
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private async Task<HttpResponseMessage> HandlePatchFileAsync(HttpRequestMessage request, string fileId)
        {
            string body = request.Content != null ? await request.Content.ReadAsStringAsync() : "{}";
            lock (_lock)
            {
                if (!_files.TryGetValue(fileId, out var f))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (body.Contains("\"trashed\":true", StringComparison.OrdinalIgnoreCase))
                {
                    f.Trashed = true;
                    TotalTrashPatches++;
                }

                return JsonResponse(SerializeFileItem(f));
            }
        }

        private static object SerializeFileItem(StoredFile f) => new
        {
            id = f.Id,
            name = f.Name,
            mimeType = f.MimeType,
            size = f.Content.Length,
            md5Checksum = f.MimeType == "application/vnd.google-apps.folder" ? null : f.Md5,
            createdTime = f.CreatedTime,
            modifiedTime = f.ModifiedTime,
            driveId = f.DriveId,
            parents = f.Parents
        };

        private static HttpResponseMessage JsonResponse(object obj)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json")
            };
        }

        private static string? ExtractQueryParam(string query, string key)
        {
            string trimmed = query.TrimStart('?');
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }
            return null;
        }
    }
}
