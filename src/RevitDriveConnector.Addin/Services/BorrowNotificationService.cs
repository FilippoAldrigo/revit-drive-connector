using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Services
{
    public class BorrowGrantExternalHandler : IExternalEventHandler
    {
        private static readonly NamedPipeIpcClient IpcClient = new();
        public ConcurrentQueue<BorrowRequest> PendingGrants { get; } = new();

        public void Execute(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            string username = doc.Application.Username;

            bool relinquishedAny = false;
            while (PendingGrants.TryDequeue(out var req))
            {
                try
                {
                    if (!relinquishedAny)
                    {
                        var relOptions = new RelinquishOptions(false)
                        {
                            CheckedOutElements = true,
                            StandardWorksets = false,
                            ViewWorksets = false,
                            FamilyWorksets = false,
                            UserWorksets = true
                        };
                        WorksharingUtils.RelinquishOwnership(doc, relOptions, new TransactWithCentralOptions());
                        relinquishedAny = true;
                    }

                    _ = Task.Run(async () =>
                    {
                        if (!string.IsNullOrEmpty(centralPath))
                        {
                            await IpcClient.SendRequestAsync(new IpcRequest
                            {
                                Command = IpcCommand.UploadProjectChanges,
                                PayloadJson = JsonSerializer.Serialize(new
                                {
                                    ProjectId = req.ProjectId,
                                    CentralPath = centralPath,
                                    LockToken = "",
                                    User = username
                                })
                            }, timeoutMs: 60000);
                        }

                        await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.RespondToBorrowRequest,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = req.ProjectId,
                                RequestId = req.RequestId,
                                Status = "Granted",
                                User = username
                            })
                        }, timeoutMs: 10000);
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[BorrowGrantExternalHandler] Error granting borrow: {ex.Message}");
                }
            }
        }

        public string GetName() => "RevitDriveConnector_BorrowGrantHandler";
    }

    public static class BorrowNotificationService
    {
        private static readonly NamedPipeIpcClient IpcClient = new();
        private static readonly HashSet<string> NotifiedRequestIds = new();
        private static readonly List<BorrowToastWindow> ActiveToasts = new();
        private static readonly object SyncLock = new();

        private static BorrowGrantExternalHandler? _grantHandler;
        private static ExternalEvent? _grantExternalEvent;

        private static DateTime _lastCheckTime = DateTime.MinValue;
        private static bool _isChecking = false;

        public static void InitializeExternalEvent()
        {
            if (_grantExternalEvent == null)
            {
                _grantHandler = new BorrowGrantExternalHandler();
                _grantExternalEvent = ExternalEvent.Create(_grantHandler);
            }
        }

        public static void QueueGrantAndRelinquish(BorrowRequest request)
        {
            if (_grantHandler != null && _grantExternalEvent != null)
            {
                _grantHandler.PendingGrants.Enqueue(request);
                _grantExternalEvent.Raise();
            }
        }

        public static void RelinquishAndGrantImmediate(Document doc, BorrowRequest request, string username)
        {
            try
            {
                var relOptions = new RelinquishOptions(false)
                {
                    CheckedOutElements = true,
                    StandardWorksets = false,
                    ViewWorksets = false,
                    FamilyWorksets = false,
                    UserWorksets = true
                };
                WorksharingUtils.RelinquishOwnership(doc, relOptions, new TransactWithCentralOptions());

                string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
                if (!string.IsNullOrEmpty(centralPath))
                {
                    var upTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.UploadProjectChanges,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            ProjectId = request.ProjectId,
                            CentralPath = centralPath,
                            LockToken = "",
                            User = username
                        })
                    }, timeoutMs: 60000));
                    upTask.Wait();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BorrowNotificationService] RelinquishAndGrantImmediate notice: {ex.Message}");
            }
        }

        public static void OnIdlingCheck(UIApplication uiapp)
        {
            if (_isChecking) return;
            if ((DateTime.UtcNow - _lastCheckTime).TotalSeconds < 3.5) return;

            _lastCheckTime = DateTime.UtcNow;

            try
            {
                var doc = uiapp.ActiveUIDocument?.Document;
                if (doc == null || !doc.IsWorkshared) return;

                string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
                if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

                string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
                string username = doc.Application.Username;

                if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(username)) return;

                _isChecking = true;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await CheckRequestsAsync(projectId, username);
                    }
                    finally
                    {
                        _isChecking = false;
                    }
                });
            }
            catch
            {
                _isChecking = false;
            }
        }

        private static async Task CheckRequestsAsync(string projectId, string username)
        {
            var payload = JsonSerializer.Serialize(new { ProjectId = projectId, User = username });

            var respInc = await IpcClient.SendRequestAsync(new IpcRequest
            {
                Command = IpcCommand.GetPendingBorrowRequests,
                PayloadJson = payload
            }, timeoutMs: 3000);

            if (respInc.Success && !string.IsNullOrEmpty(respInc.DataJson))
            {
                var list = JsonSerializer.Deserialize<List<BorrowRequest>>(respInc.DataJson);
                if (list != null)
                {
                    foreach (var req in list)
                    {
                        bool shouldNotify = false;
                        lock (SyncLock)
                        {
                            if (!NotifiedRequestIds.Contains(req.RequestId))
                            {
                                NotifiedRequestIds.Add(req.RequestId);
                                shouldNotify = true;
                            }
                        }

                        if (shouldNotify)
                        {
                            App.UiDispatcher?.Invoke(() =>
                            {
                                ShowToast(req, username, isInformational: false);
                            });
                        }
                    }
                }
            }

            var respOut = await IpcClient.SendRequestAsync(new IpcRequest
            {
                Command = IpcCommand.GetOutgoingBorrowRequests,
                PayloadJson = payload
            }, timeoutMs: 3000);

            if (respOut.Success && !string.IsNullOrEmpty(respOut.DataJson))
            {
                var list = JsonSerializer.Deserialize<List<BorrowRequest>>(respOut.DataJson);
                if (list != null)
                {
                    foreach (var req in list)
                    {
                        if (req.Status == BorrowRequestStatus.Pending) continue;

                        string notifyKey = $"{req.RequestId}_{req.Status}";
                        bool shouldNotify = false;

                        lock (SyncLock)
                        {
                            if (!NotifiedRequestIds.Contains(notifyKey))
                            {
                                NotifiedRequestIds.Add(notifyKey);
                                if (req.RespondedAtUtc.HasValue && (DateTime.UtcNow - req.RespondedAtUtc.Value).TotalMinutes < 20)
                                {
                                    shouldNotify = true;
                                }
                            }
                        }

                        if (shouldNotify)
                        {
                            App.UiDispatcher?.Invoke(() =>
                            {
                                ShowToast(req, username, isInformational: true);
                            });
                        }
                    }
                }
            }
        }

        private static void ShowToast(BorrowRequest req, string username, bool isInformational)
        {
            try
            {
                int stackIndex = 0;
                lock (SyncLock)
                {
                    stackIndex = ActiveToasts.Count;
                }

                var toast = new BorrowToastWindow(req, username, stackIndex, isInformational);
                toast.ToastClosed += (s, e) =>
                {
                    lock (SyncLock)
                    {
                        ActiveToasts.Remove(toast);
                    }
                };

                lock (SyncLock)
                {
                    ActiveToasts.Add(toast);
                }

                toast.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BorrowNotificationService] Errore visualizzazione toast: {ex.Message}");
            }
        }

        public static void ClearSession()
        {
            lock (SyncLock)
            {
                NotifiedRequestIds.Clear();
                foreach (var toast in ActiveToasts.ToArray())
                {
                    try { toast.Close(); } catch { }
                }
                ActiveToasts.Clear();
            }
        }
    }
}
