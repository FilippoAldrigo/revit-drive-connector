using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Services
{
    public static class SynchronizationCoordinator
    {
        private static readonly NamedPipeIpcClient IpcClient = new();
        public static string? CurrentLockToken { get; private set; }
        public static string? ActiveProjectId { get; set; }
        public static bool SuppressNextAutoReload { get; set; } = false;
        public static bool LastSyncCancelled { get; set; } = false;
        private static CancellationTokenSource? _heartbeatCts;

        public static void OnDocumentOpening(object? sender, DocumentOpeningEventArgs e)
        {
        }

        public static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsWorkshared) return;

                string centralPath = GetCentralModelPath(doc);
                if (string.IsNullOrEmpty(centralPath)) return;

                string projectId = ExtractProjectName(centralPath);
                if (IsDriveManagedPath(centralPath))
                {
                    ActiveProjectId = projectId;
                }

                string title = doc.Title;
                string user = doc.Application.Username;

                _ = Task.Run(async () =>
                {
                    await IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.NotifyDocumentOpened,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            ProjectId = projectId,
                            DocumentTitle = title,
                            CentralPath = centralPath,
                            User = user,
                            Machine = Environment.MachineName
                        })
                    });
                });
            }
            catch
            {
            }
        }

        public static void OnDocumentClosing(object? sender, DocumentClosingEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null) return;

                string centralPath = doc.IsWorkshared ? GetCentralModelPath(doc) : string.Empty;
                string projectId = !string.IsNullOrEmpty(centralPath) ? ExtractProjectName(centralPath) : doc.Title;
                string title = doc.Title;
                string user = doc.Application.Username;

                _ = Task.Run(async () =>
                {
                    await IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.NotifyDocumentClosed,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            ProjectId = projectId,
                            DocumentTitle = title,
                            User = user,
                            Machine = Environment.MachineName
                        })
                    });
                });
            }
            catch
            {
            }
        }

        public static void OnDocumentReloadingLatest(object? sender, DocumentReloadingLatestEventArgs e)
        {
            var doc = e.Document;
            if (doc == null || !doc.IsWorkshared) return;

            string centralPath = GetCentralModelPath(doc);
            if (!IsDriveManagedPath(centralPath)) return;

            AlignCentralFromCloudSync(doc);
        }

        public static bool AlignCentralFromCloudSync(Document doc)
        {
            try
            {
                string centralPath = GetCentralModelPath(doc);
                if (!IsDriveManagedPath(centralPath)) return true;

                string projectId = ExtractProjectName(centralPath);
                string user = doc.Application.Username;

                var payload = JsonSerializer.Serialize(new
                {
                    ProjectId = projectId,
                    User = user
                });

                var task = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.VerifySyncIntegrity,
                    PayloadJson = payload
                }, timeoutMs: 120000));

                task.Wait();
                var resp = task.Result;
                if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                {
                    var result = JsonSerializer.Deserialize<SyncIntegrityResult>(resp.DataJson);
                    return result?.IsAligned ?? true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SynchronizationCoordinator] AlignCentralFromCloudSync warning: {ex.Message}");
            }

            return false;
        }

        private static void StartLockHeartbeat(string projectId, string lockToken, string user)
        {
            StopLockHeartbeat();
            _heartbeatCts = new CancellationTokenSource();
            var ct = _heartbeatCts.Token;

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(25000, ct);
                        if (ct.IsCancellationRequested) break;

                        await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.HeartbeatSyncLock,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = projectId,
                                LockToken = lockToken,
                                User = user
                            })
                        }, timeoutMs: 5000, ct: ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }
                }
            }, ct);
        }

        private static void StopLockHeartbeat()
        {
            try
            {
                _heartbeatCts?.Cancel();
                _heartbeatCts?.Dispose();
            }
            catch { }
            finally
            {
                _heartbeatCts = null;
            }
        }

        public static void OnDocumentSynchronizingWithCentral(object? sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            var doc = e.Document;
            if (doc == null || !doc.IsWorkshared) return;

            string centralPath = GetCentralModelPath(doc);
            if (!IsDriveManagedPath(centralPath))
            {
                return;
            }

            LastSyncCancelled = false;

            try
            {
                string projectId = ExtractProjectName(centralPath);
                string user = doc.Application.Username;

                var lockRequest = new LockRequest
                {
                    ProjectId = projectId,
                    User = user,
                    Machine = Environment.MachineName,
                    TimeoutSeconds = 90
                };

                var ipcReq = new IpcRequest
                {
                    Command = IpcCommand.AcquireSyncLock,
                    PayloadJson = JsonSerializer.Serialize(lockRequest)
                };

                var respTask = Task.Run(() => IpcClient.SendRequestAsync(ipcReq, timeoutMs: 15000));
                respTask.Wait();
                var resp = respTask.Result;

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    LastSyncCancelled = true;
                    if (e.Cancellable)
                    {
                        try { e.Cancel(); } catch { }
                    }
                    System.Windows.MessageBox.Show(
                        $"Impossibile acquisire il lock di sincronizzazione con Google Drive:\n{resp.ErrorMessage ?? "Risposta vuota"}\n\nLa sincronizzazione è stata annullata per proteggere il modello.",
                        "Drive Sync Bloccato",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                var lockResp = JsonSerializer.Deserialize<LockResponse>(resp.DataJson);
                if (lockResp == null || lockResp.Status != SyncLockStatus.LockAcquired)
                {
                    LastSyncCancelled = true;
                    if (e.Cancellable)
                    {
                        try { e.Cancel(); } catch { }
                    }
                    string holder = lockResp?.CurrentHolder ?? "un altro utente";
                    System.Windows.MessageBox.Show(
                        $"Il progetto è attualmente in fase di sincronizzazione da parte di: {holder} ({lockResp?.CurrentHolderMachine}).\n\nAttendi qualche secondo che termini e riprova.",
                        "Drive Sync Occupato",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                CurrentLockToken = lockResp.LockToken;
                StartLockHeartbeat(projectId, CurrentLockToken ?? "", user);

                var gatekeeper = new UI.SyncGatekeeperDialog(projectId, user);
                bool? gateResult = gatekeeper.ShowDialog();

                if (gateResult != true || !gatekeeper.IsAlignedAndReady)
                {
                    StopLockHeartbeat();
                    string tokenToRelease = CurrentLockToken ?? "";
                    CurrentLockToken = null;
                    LastSyncCancelled = true;

                    if (e.Cancellable)
                    {
                        try { e.Cancel(); } catch { }
                    }

                    _ = Task.Run(async () =>
                    {
                        await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.ReleaseSyncLock,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = projectId,
                                LockToken = tokenToRelease,
                                User = user
                            })
                        });
                    });

                    if (!gatekeeper.UserCancelled)
                    {
                        System.Windows.MessageBox.Show(
                            "La sincronizzazione è stata interrotta per proteggere l'integrità del modello (i file di Google Drive non sono ancora allineati). Riprova tra qualche istante.",
                            "Sincronizzazione Annullata",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                StopLockHeartbeat();
                LastSyncCancelled = true;
                if (e.Cancellable)
                {
                    try { e.Cancel(); } catch { }
                }
                System.Windows.MessageBox.Show(
                    $"Errore critico durante la pre-sincronizzazione: {ex.Message}",
                    "Errore Drive Connector",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        public static void OnDocumentSynchronizedWithCentral(object? sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            StopLockHeartbeat();

            if (LastSyncCancelled)
            {
                LastSyncCancelled = false;
                return;
            }

            var doc = e.Document;
            if (doc == null || !doc.IsWorkshared) return;

            string centralPath = GetCentralModelPath(doc);
            if (!IsDriveManagedPath(centralPath)) return;

            try
            {
                string token = CurrentLockToken ?? string.Empty;
                CurrentLockToken = null;
                string projectId = ExtractProjectName(centralPath);
                string user = doc.Application.Username;

                var uploadPayload = JsonSerializer.Serialize(new
                {
                    ProjectId = projectId,
                    CentralPath = centralPath,
                    LockToken = token,
                    User = user
                });

                var uploadTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.UploadProjectChanges,
                    PayloadJson = uploadPayload
                }, timeoutMs: 300000));

                uploadTask.Wait();
                var uploadResp = uploadTask.Result;
                if (!uploadResp.Success)
                {
                    System.Windows.MessageBox.Show(
                        $"La sincronizzazione locale è riuscita, ma si è verificato un problema nel caricamento su Drive:\n{uploadResp.ErrorMessage}",
                        "Avviso Upload Drive",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"La sincronizzazione locale è riuscita, ma si è verificato un errore nel caricamento su Drive: {ex.Message}",
                    "Avviso Upload Drive",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        public static string ExtractProjectName(string centralPath)
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

        public static string GetCentralModelPath(Document doc)
        {
            try
            {
                var modelPath = doc.GetWorksharingCentralModelPath();
                if (modelPath == null) return string.Empty;
                return ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static bool IsDriveManagedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var cfg = AppConfig.Load();
            if (!string.IsNullOrEmpty(cfg.LocalCachePath) &&
                path.IndexOf(cfg.LocalCachePath, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return path.IndexOf("RevitDriveCache", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
