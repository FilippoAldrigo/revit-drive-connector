using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Core.Utils
{
    public class CachedFileHashEntry
    {
        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("lastWriteTicksUtc")]
        public long LastWriteTicksUtc { get; set; }

        [JsonPropertyName("md5")]
        public string Md5 { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Persistent + in-memory hash cache keyed by (path, size, lastWriteTimeUtc).
    /// Computes MD5 and SHA-256 in a single sequential stream pass, avoiding redundant multi-GB disk reads
    /// on unchanged Revit _backup files during sync.
    /// </summary>
    public static class FileHashCache
    {
        private static readonly ConcurrentDictionary<string, CachedFileHashEntry> MemoryCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> LoadedProjects = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object PersistLock = new();

        public static void LoadProjectCache(string localProjectDir)
        {
            if (string.IsNullOrWhiteSpace(localProjectDir)) return;
            string cachePath = Path.Combine(localProjectDir, ".hashcache.json");
            if (!LoadedProjects.TryAdd(cachePath, true)) return;

            try
            {
                if (!File.Exists(cachePath)) return;
                string json = File.ReadAllText(cachePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, CachedFileHashEntry>>(json);
                if (dict == null) return;

                foreach (var kvp in dict)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value != null)
                    {
                        MemoryCache[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FileHashCache", $"Impossibile leggere cache hash '{cachePath}'", ex);
            }
        }

        public static void SaveProjectCache(string localProjectDir, IEnumerable<string> activeFiles)
        {
            if (string.IsNullOrWhiteSpace(localProjectDir)) return;
            try
            {
                var snapshot = new Dictionary<string, CachedFileHashEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in activeFiles)
                {
                    string full = Path.GetFullPath(path);
                    if (MemoryCache.TryGetValue(full, out var entry))
                    {
                        snapshot[full] = entry;
                    }
                }

                string cachePath = Path.Combine(localProjectDir, ".hashcache.json");
                Directory.CreateDirectory(localProjectDir);
                string temp = cachePath + ".tmp." + Guid.NewGuid().ToString("N");
                string json = JsonSerializer.Serialize(snapshot);

                lock (PersistLock)
                {
                    File.WriteAllText(temp, json);
                    File.Move(temp, cachePath, true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("FileHashCache", $"Impossibile salvare cache hash per '{localProjectDir}'", ex);
            }
        }

        public static void InvalidateFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            MemoryCache.TryRemove(Path.GetFullPath(filePath), out _);
        }

        public static void ClearAll()
        {
            MemoryCache.Clear();
            LoadedProjects.Clear();
        }

        public static async Task<(string Md5, string Sha256)> GetOrComputeHashesAsync(string filePath, CancellationToken ct = default)
        {
            string fullPath = Path.GetFullPath(filePath);
            var fi = new FileInfo(fullPath);
            if (!fi.Exists)
            {
                throw new FileNotFoundException("File non trovato per calcolo hash.", fullPath);
            }

            long size = fi.Length;
            long ticks = fi.LastWriteTimeUtc.Ticks;

            if (MemoryCache.TryGetValue(fullPath, out var cached) &&
                cached.Size == size &&
                cached.LastWriteTicksUtc == ticks &&
                !string.IsNullOrEmpty(cached.Md5) &&
                !string.IsNullOrEmpty(cached.Sha256))
            {
                return (cached.Md5, cached.Sha256);
            }

            var (md5, sha256) = await ComputeBothHashesSinglePassAsync(fullPath, ct);

            MemoryCache[fullPath] = new CachedFileHashEntry
            {
                Size = size,
                LastWriteTicksUtc = ticks,
                Md5 = md5,
                Sha256 = sha256
            };

            return (md5, sha256);
        }

        public static async Task<string> GetOrComputeMd5Async(string filePath, CancellationToken ct = default)
        {
            var (md5, _) = await GetOrComputeHashesAsync(filePath, ct);
            return md5;
        }

        public static async Task<string> GetOrComputeSha256Async(string filePath, CancellationToken ct = default)
        {
            var (_, sha256) = await GetOrComputeHashesAsync(filePath, ct);
            return sha256;
        }

        /// <summary>
        /// Reads the file once with a 1 MB sequential buffer and computes both MD5 and SHA-256 simultaneously.
        /// </summary>
        public static async Task<(string Md5, string Sha256)> ComputeBothHashesSinglePassAsync(string filePath, CancellationToken ct = default)
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            byte[] buffer = new byte[1024 * 1024];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                md5.AppendData(buffer, 0, bytesRead);
                sha256.AppendData(buffer, 0, bytesRead);
            }

            string md5Hex = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
            string shaHex = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            return (md5Hex, shaHex);
        }
    }
}
