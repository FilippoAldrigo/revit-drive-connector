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
        public static long? CurrentExpectedBaseVersion { get; private set; }
        public static string? CurrentExpectedBaseHash { get; private set; }
        public static string? ActiveProjectId { get; set; }
        public static bool SuppressNextAutoReload { get; set; } = false;
        public static bool SuppressNextReloadAlignment { get; set; } = false;
        public static bool LastSyncCancelled { get; set; } = false;
        public static bool LastUploadFailed { get; set; } = false;
        public static string? LastUploadError { get; set; }
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

                LiveElementLockService.OnDocumentClosing(doc);

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

            if (SuppressNextReloadAlignment)
            {
                SuppressNextReloadAlignment = false;
                return;
            }

            if (!AlignCentralFromCloudSync(doc))
            {
                LiveElementLockService.IsSyncOrReloadActive = false;
                if (e.Cancellable)
                {
                    try { e.Cancel(); } catch { }
                }
                System.Windows.MessageBox.Show(
                    "Impossibile scaricare l'allineamento aggiornato da Google Drive per eseguire 'Ricarica Ultime Modifiche'. Verifica la connessione e l'account Google.",
                    "Drive Connector - Errore Allineamento",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
            else
            {
                LiveElementLockService.IsSyncOrReloadActive = true;
            }
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
            LastUploadFailed = false;
            LastUploadError = null;
            CurrentExpectedBaseVersion = null;
            CurrentExpectedBaseHash = null;

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

                var respTask = Task.Run(() => IpcClient.SendRequestAsync(ipcReq, timeoutMs: 25000));
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
                    string msg = !string.IsNullOrWhiteSpace(lockResp?.Message)
                        ? lockResp.Message
                        : $"Il progetto è attualmente in fase di sincronizzazione da parte di: {holder} ({lockResp?.CurrentHolderMachine}).\n\nAttendi qualche secondo che termini e riprova.";
                    System.Windows.MessageBox.Show(
                        msg,
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
                    CurrentExpectedBaseVersion = null;
                    CurrentExpectedBaseHash = null;
                    LastSyncCancelled = true;

                    if (e.Cancellable)
                    {
                        try { e.Cancel(); } catch { }
                    }

                    ReleaseLockSync(projectId, tokenToRelease, user);

                    if (!gatekeeper.UserCancelled)
                    {
                        string detail = !string.IsNullOrWhiteSpace(gatekeeper.ErrorMessage)
                            ? $"\n\nDettaglio: {gatekeeper.ErrorMessage}"
                            : "";
                        System.Windows.MessageBox.Show(
                            $"La sincronizzazione è stata interrotta per proteggere l'integrità del modello (i file di Google Drive non sono allineati). Riprova tra qualche istante.{detail}",
                            "Sincronizzazione Annullata",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    return;
                }

                CurrentExpectedBaseVersion = gatekeeper.CloudVersion;
                CurrentExpectedBaseHash = gatekeeper.ExpectedHash;
                LiveElementLockService.IsSyncOrReloadActive = true;
            }
            catch (Exception ex)
            {
                LiveElementLockService.IsSyncOrReloadActive = false;
                StopLockHeartbeat();
                string tokenToRelease = CurrentLockToken ?? "";
                CurrentLockToken = null;
                CurrentExpectedBaseVersion = null;
                CurrentExpectedBaseHash = null;
                LastSyncCancelled = true;

                if (!string.IsNullOrEmpty(tokenToRelease))
                {
                    try { ReleaseLockSync(ExtractProjectName(centralPath), tokenToRelease, doc.Application.Username); } catch { }
                }

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

            string token = CurrentLockToken ?? string.Empty;
            long? expectedBaseVersion = CurrentExpectedBaseVersion;
            string? expectedBaseHash = CurrentExpectedBaseHash;
            CurrentLockToken = null;
            CurrentExpectedBaseVersion = null;
            CurrentExpectedBaseHash = null;

            try
            {
                var doc = e.Document;
                if (doc == null || !doc.IsWorkshared) return;

                string centralPath = GetCentralModelPath(doc);
                if (!IsDriveManagedPath(centralPath)) return;

                string projectId = ExtractProjectName(centralPath);
                string user = doc.Application.Username;

                if (LastSyncCancelled)
                {
                    LastSyncCancelled = false;
                    if (!string.IsNullOrEmpty(token))
                    {
                        ReleaseLockSync(projectId, token, user);
                    }
                    return;
                }

                if (e.Status != RevitAPIEventStatus.Succeeded)
                {
                    LastSyncCancelled = e.Status == RevitAPIEventStatus.Cancelled;
                    LastUploadFailed = e.Status == RevitAPIEventStatus.Failed;
                    LastUploadError = $"Sincronizzazione Revit non completata ({e.Status}).";
                    if (!string.IsNullOrEmpty(token))
                    {
                        ReleaseLockSync(projectId, token, user);
                    }
                    return;
                }

                try
                {
                    var uploadPayload = JsonSerializer.Serialize(new
                    {
                        ProjectId = projectId,
                        CentralPath = centralPath,
                        LockToken = token,
                        User = user,
                        ExpectedBaseVersion = expectedBaseVersion,
                        ExpectedBaseHash = expectedBaseHash
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
                        LastUploadFailed = true;
                        LastUploadError = uploadResp.ErrorMessage;
                        System.Windows.MessageBox.Show(
                            $"La sincronizzazione locale in Revit è riuscita, ma il caricamento su Google Drive è stato bloccato/fallito:\n\n{uploadResp.ErrorMessage}",
                            "Avviso Upload Drive",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                    else
                    {
                        LiveElementLockService.OnDocumentSynchronized(doc);
                    }
                }
                catch (Exception ex)
                {
                    LastUploadFailed = true;
                    LastUploadError = ex.Message;
                    if (!string.IsNullOrEmpty(token))
                    {
                        ReleaseLockSync(projectId, token, user);
                    }
                    System.Windows.MessageBox.Show(
                        $"La sincronizzazione locale è riuscita, ma si è verificato un errore nel caricamento su Drive: {ex.Message}",
                        "Avviso Upload Drive",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            }
            finally
            {
                LiveElementLockService.IsSyncOrReloadActive = false;
            }
        }

        private static void ReleaseLockSync(string projectId, string lockToken, string user)
        {
            try
            {
                var t = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.ReleaseSyncLock,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        ProjectId = projectId,
                        LockToken = lockToken,
                        User = user
                    })
                }, timeoutMs: 10000));
                t.Wait();
            }
            catch { }
        }

        /// <summary>
        /// Esegue un'operazione sul modello centrale (es. Relinquish/Checkout) in sicurezza:
        /// acquisisce il lock, allinea da Google Drive, ricarica il locale se necessario,
        /// esegue l'azione Revit e carica le modifiche con verifica ottimistica.
        /// </summary>
        public static bool ExecuteLockedCentralOperation(
            Document doc,
            Func<bool> revitAction,
            out string errorMessage,
            bool reloadIfCloudUpdated = true)
        {
            errorMessage = string.Empty;
            if (doc == null || !doc.IsWorkshared)
            {
                errorMessage = "Il documento non è condiviso.";
                return false;
            }

            string centralPath = GetCentralModelPath(doc);
            if (!IsDriveManagedPath(centralPath))
            {
                try
                {
                    revitAction();
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    return false;
                }
            }

            string projectId = ExtractProjectName(centralPath);
            string user = doc.Application.Username;
            string? lockToken = null;
            bool lockHandledByUpload = false;

            try
            {
                var lockReq = new LockRequest
                {
                    ProjectId = projectId,
                    User = user,
                    Machine = Environment.MachineName,
                    TimeoutSeconds = 90
                };

                var lockTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.AcquireSyncLock,
                    PayloadJson = JsonSerializer.Serialize(lockReq)
                }, timeoutMs: 25000));
                lockTask.Wait();
                var lockIpcResp = lockTask.Result;

                if (!lockIpcResp.Success || string.IsNullOrEmpty(lockIpcResp.DataJson))
                {
                    errorMessage = lockIpcResp.ErrorMessage ?? "Impossibile acquisire il lock di sincronizzazione su Google Drive.";
                    return false;
                }

                var lockResp = JsonSerializer.Deserialize<LockResponse>(lockIpcResp.DataJson);
                if (lockResp == null || lockResp.Status != SyncLockStatus.LockAcquired || string.IsNullOrEmpty(lockResp.LockToken))
                {
                    errorMessage = !string.IsNullOrWhiteSpace(lockResp?.Message)
                        ? lockResp.Message
                        : $"Progetto occupato da {lockResp?.CurrentHolder ?? "un altro utente"}. Riprova tra qualche secondo.";
                    return false;
                }

                lockToken = lockResp.LockToken;
                StartLockHeartbeat(projectId, lockToken, user);

                SyncIntegrityResult? alignedResult = null;
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    var verifyTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.VerifySyncIntegrity,
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            ProjectId = projectId,
                            User = user
                        })
                    }, timeoutMs: 180000));
                    verifyTask.Wait();
                    var verifyResp = verifyTask.Result;

                    if (!verifyResp.Success || string.IsNullOrEmpty(verifyResp.DataJson))
                    {
                        errorMessage = verifyResp.ErrorMessage ?? "Errore durante l'allineamento da Google Drive.";
                        return false;
                    }

                    alignedResult = JsonSerializer.Deserialize<SyncIntegrityResult>(verifyResp.DataJson);
                    if (alignedResult == null)
                    {
                        errorMessage = "Risposta di allineamento non valida.";
                        return false;
                    }

                    if (alignedResult.IsAligned)
                    {
                        break;
                    }

                    if (!alignedResult.IsWaitingForDownload)
                    {
                        errorMessage = alignedResult.Message;
                        return false;
                    }

                    Thread.Sleep(1500);
                }

                if (alignedResult == null || !alignedResult.IsAligned)
                {
                    errorMessage = alignedResult?.Message ?? "Timeout durante l'allineamento da Google Drive.";
                    return false;
                }

                if (alignedResult.WasUpdatedFromCloud || reloadIfCloudUpdated)
                {
                    try
                    {
                        SuppressNextReloadAlignment = true;
                        LiveElementLockService.IsSyncOrReloadActive = true;
                        doc.ReloadLatest(new ReloadLatestOptions());
                    }
                    catch (Exception reloadEx)
                    {
                        errorMessage = $"Impossibile aggiornare il modello locale con le modifiche del collega prima dell'operazione ({reloadEx.Message}). Esegui prima 'Sincronizza Drive STC'.";
                        return false;
                    }
                    finally
                    {
                        SuppressNextReloadAlignment = false;
                        LiveElementLockService.IsSyncOrReloadActive = false;
                    }
                }

                bool shouldUpload = revitAction();
                if (!shouldUpload)
                {
                    LiveElementLockService.OnDocumentSynchronized(doc);
                    return true;
                }

                var uploadPayload = JsonSerializer.Serialize(new
                {
                    ProjectId = projectId,
                    CentralPath = centralPath,
                    LockToken = lockToken,
                    User = user,
                    ExpectedBaseVersion = alignedResult.CloudVersion,
                    ExpectedBaseHash = alignedResult.ExpectedHash
                });

                var uploadTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.UploadProjectChanges,
                    PayloadJson = uploadPayload
                }, timeoutMs: 300000));
                uploadTask.Wait();
                var uploadResp = uploadTask.Result;
                lockHandledByUpload = true;

                if (!uploadResp.Success)
                {
                    errorMessage = uploadResp.ErrorMessage ?? "Errore durante il caricamento su Google Drive.";
                    return false;
                }

                LiveElementLockService.OnDocumentSynchronized(doc);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
            finally
            {
                StopLockHeartbeat();
                if (!lockHandledByUpload && !string.IsNullOrEmpty(lockToken))
                {
                    ReleaseLockSync(projectId, lockToken, user);
                }
            }
        }

        public static string ExtractProjectName(string centralPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(centralPath))
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
            }
            catch { }

            if (!string.IsNullOrEmpty(ActiveProjectId))
            {
                string p = ActiveProjectId;
                if (p.EndsWith("_Central", StringComparison.OrdinalIgnoreCase))
                    p = p.Substring(0, p.Length - "_Central".Length);
                return p;
            }

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
