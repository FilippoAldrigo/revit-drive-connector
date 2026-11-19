using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Core.Utils
{
    public static class BorrowRequestManager
    {
        private static readonly object _lock = new();

        public static List<BorrowRequest> LoadAllRequests(string cloudProjectDir)
        {
            lock (_lock)
            {
                try
                {
                    string filePath = Path.Combine(cloudProjectDir, ".connector", "borrow_requests.json");
                    if (!File.Exists(filePath)) return new List<BorrowRequest>();

                    string json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<List<BorrowRequest>>(json) ?? new List<BorrowRequest>();
                }
                catch
                {
                    return new List<BorrowRequest>();
                }
            }
        }

        public static void SaveAllRequests(string cloudProjectDir, List<BorrowRequest> list)
        {
            lock (_lock)
            {
                try
                {
                    string connectorDir = Path.Combine(cloudProjectDir, ".connector");
                    Directory.CreateDirectory(connectorDir);
                    string filePath = Path.Combine(connectorDir, "borrow_requests.json");

                    string tempPath = filePath + ".tmp." + Guid.NewGuid().ToString("N");
                    string json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, filePath, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BorrowRequestManager] Errore salvataggio richieste: {ex.Message}");
                }
            }
        }

        public static BorrowRequest SendRequest(string cloudProjectDir, BorrowRequest request)
        {
            var list = LoadAllRequests(cloudProjectDir);
            // Rimuoviamo eventuali richieste duplicate o vecchie per lo stesso elemento e utente
            list.RemoveAll(r => r.ElementId == request.ElementId &&
                r.RequesterUser.Equals(request.RequesterUser, StringComparison.OrdinalIgnoreCase) &&
                (r.Status == BorrowRequestStatus.Pending || r.Status == BorrowRequestStatus.Completed));

            list.Add(request);
            SaveAllRequests(cloudProjectDir, list);
            return request;
        }

        public static List<BorrowRequest> GetPendingRequestsForUser(string cloudProjectDir, string ownerUser)
        {
            var list = LoadAllRequests(cloudProjectDir);
            return list.Where(r => r.OwnerUser.Equals(ownerUser, StringComparison.OrdinalIgnoreCase) &&
                r.Status == BorrowRequestStatus.Pending).ToList();
        }

        public static List<BorrowRequest> GetOutgoingRequestsForUser(string cloudProjectDir, string requesterUser)
        {
            var list = LoadAllRequests(cloudProjectDir);
            return list.Where(r => r.RequesterUser.Equals(requesterUser, StringComparison.OrdinalIgnoreCase) &&
                r.Status != BorrowRequestStatus.Completed).ToList();
        }

        public static bool RespondToRequest(string cloudProjectDir, string requestId, BorrowRequestStatus status, string responderUser)
        {
            var list = LoadAllRequests(cloudProjectDir);
            var req = list.FirstOrDefault(r => r.RequestId == requestId);
            if (req == null) return false;

            req.Status = status;
            req.RespondedAtUtc = DateTime.UtcNow;
            req.Message = status == BorrowRequestStatus.Granted ? "Prestito concesso (elemento rilasciato dal proprietario)" : "Richiesta rifiutata";

            SaveAllRequests(cloudProjectDir, list);
            return true;
        }

        public static bool MarkRequestsCompleted(string cloudProjectDir, IEnumerable<string> requestIds)
        {
            var idSet = new HashSet<string>(requestIds, StringComparer.OrdinalIgnoreCase);
            if (idSet.Count == 0) return false;

            var list = LoadAllRequests(cloudProjectDir);
            bool changed = false;
            foreach (var req in list)
            {
                if (idSet.Contains(req.RequestId))
                {
                    req.Status = BorrowRequestStatus.Completed;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveAllRequests(cloudProjectDir, list);
            }
            return changed;
        }
    }
}
