using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace RevitDriveConnector.Core.Utils
{
    public static class FileHashHelper
    {
        public static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath)) return string.Empty;

            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            var hashBytes = await sha256.ComputeHashAsync(stream, ct);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        public static string ComputeSha256(string filePath)
        {
            if (!File.Exists(filePath)) return string.Empty;

            using var sha256 = SHA256.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        public static async Task<string> ComputeMd5Async(string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath)) return string.Empty;

            using var md5 = MD5.Create();
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
            var hashBytes = await md5.ComputeHashAsync(stream, ct);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
