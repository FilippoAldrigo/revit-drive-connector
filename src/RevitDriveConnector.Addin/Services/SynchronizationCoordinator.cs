using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

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
                    }, timeoutMs: 15000);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SynchronizationCoordinator", "OnDocumentOpened warning", ex);
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
                    }, timeoutMs: 10000);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SynchronizationCoordinator", "OnDocumentClosing warning", ex);
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
                    "Impossibile scaricare l'allineamento aggiornato da Google Drive per eseguire 'Ricarica Ultime Modifiche'. Verifica la connessione, l'account Google o eventuali upload locali pendenti.",
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

                var gatekeeper = new SyncGatekeeperDialog(projectId, user);
                bool? gateResult = gatekeeper.ShowDialog();
                if (gateResult == true && gatekeeper.IsAlignedAndReady)
                {
                    return true;
                }

                if (gatekeeper.HasPendingLocalUpload)
                {
                    return HandlePendingLocalUploadRecovery(projectId, centralPath, user, gatekeeper.PendingUploadCanRetryDirectly, gatekeeper.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SynchronizationCoordinator", "AlignCentralFromCloudSync warning", ex);
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
                        }, timeoutMs: 10000, ct: ct);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("SynchronizationCoordinator", "Heartbeat tick warning", ex);
                    }
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
            if (!IsDriveManagedPath(centralPath)) return;

            LastSyncCancelled = false;
            LastUploadFailed = false;
            LastUploadError = null;
            CurrentExpectedBaseVersion = null;
            CurrentExpectedBaseHash = null;

            string projectId = ExtractProjectName(centralPath);
            string user = doc.Application.Username;
            string localProjectDir = Path.Combine(AppConfig.Load().LocalCachePath, projectId);

            try
            {
                // 0. Check for pending un-uploaded local sync BEFORE doing anything destructive!
                var pending = CentralSnapshotHelper.ReadPendingUpload(localProjectDir);
                if (pending != null)
                {
                    bool recovered = HandlePendingLocalUploadRecovery(projectId, centralPath, user, canRetryDirectlyHint: null, detailMessage: pending.LastError);
                    if (!recovered)
                    {
                        LastSyncCancelled = true;
                        if (e.Cancellable) try { e.Cancel(); } catch { }
                        return;
                    }
                }

                // 1. Acquire distributed Google Drive sync lock
                var cfg = AppConfig.Load();
                var lockRequest = new LockRequest
                {
                    ProjectId = projectId,
                    User = user,
                    Machine = Environment.MachineName,
                    TimeoutSeconds = Math.Max(120, cfg.SyncTimeoutSeconds)
                };

                var respTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.AcquireSyncLock,
                    PayloadJson = JsonSerializer.Serialize(lockRequest)
                }, timeoutMs: 30000));
                respTask.Wait();
                var resp = respTask.Result;

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    LastSyncCancelled = true;
                    if (e.Cancellable) try { e.Cancel(); } catch { }
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
                    if (e.Cancellable) try { e.Cancel(); } catch { }
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

                // 2. Align local Central cache with Google Drive (showing live progress)
                var gatekeeper = new SyncGatekeeperDialog(projectId, user);
                bool? gateResult = gatekeeper.ShowDialog();

                if (gateResult != true || !gatekeeper.IsAlignedAndReady)
                {
                    StopLockHeartbeat();
                    string tokenToRelease = CurrentLockToken ?? "";
                    CurrentLockToken = null;
                    CurrentExpectedBaseVersion = null;
                    CurrentExpectedBaseHash = null;
                    LastSyncCancelled = true;

                    if (e.Cancellable) try { e.Cancel(); } catch { }
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

                // 3. Anti-corruption safeguard: snapshot & mark pending upload BEFORE Revit mutates local Central!
                CentralSnapshotHelper.CreatePreSyncSnapshot(localProjectDir);
                CentralSnapshotHelper.MarkPendingUpload(localProjectDir, new PendingUploadMarker
                {
                    ProjectId = projectId,
                    CentralPath = centralPath,
                    User = user,
                    ExpectedBaseVersion = CurrentExpectedBaseVersion,
                    ExpectedBaseHash = CurrentExpectedBaseHash,
                    CreatedAtUtc = DateTime.UtcNow
                });

                LiveElementLockService.IsSyncOrReloadActive = true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("SynchronizationCoordinator", $"Errore critico pre-sync su '{projectId}'", ex);
                LiveElementLockService.IsSyncOrReloadActive = false;
                StopLockHeartbeat();
                string tokenToRelease = CurrentLockToken ?? "";
                CurrentLockToken = null;
                CurrentExpectedBaseVersion = null;
                CurrentExpectedBaseHash = null;
                LastSyncCancelled = true;

                if (!string.IsNullOrEmpty(tokenToRelease))
                {
                    try { ReleaseLockSync(projectId, tokenToRelease, user); } catch { }
                }

                if (e.Cancellable) try { e.Cancel(); } catch { }
                System.Windows.MessageBox.Show(
                    $"Errore critico durante la pre-sincronizzazione: {ex.Message}",
                    "Errore Drive Connector",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        public static void OnDocumentSynchronizedWithCentral(object? sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            // CRITICAL FIX: DO NOT stop the lock heartbeat before the cloud upload completes!
            // Keep the distributed lock alive throughout the entire chunked upload (critical for 500 MB - 1 GB models).
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
                string localProjectDir = Path.Combine(AppConfig.Load().LocalCachePath, projectId);

                if (LastSyncCancelled)
                {
                    LastSyncCancelled = false;
                    CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
                    if (!string.IsNullOrEmpty(token)) ReleaseLockSync(projectId, token, user);
                    return;
                }

                if (e.Status != RevitAPIEventStatus.Succeeded)
                {
                    LastSyncCancelled = e.Status == RevitAPIEventStatus.Cancelled;
                    LastUploadFailed = e.Status == RevitAPIEventStatus.Failed;
                    LastUploadError = $"Sincronizzazione Revit non completata ({e.Status}).";
                    CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
                    if (!string.IsNullOrEmpty(token)) ReleaseLockSync(projectId, token, user);
                    return;
                }

                // Upload with live chunked progress window and interactive retry loop if network fails!
                bool uploaded = ExecuteUploadWithProgressAndRetry(
                    projectId,
                    centralPath,
                    user,
                    token,
                    expectedBaseVersion,
                    expectedBaseHash,
                    localProjectDir);

                if (uploaded)
                {
                    LiveElementLockService.OnDocumentSynchronized(doc);
                }
            }
            finally
            {
                StopLockHeartbeat();
                LiveElementLockService.IsSyncOrReloadActive = false;
            }
        }

        /// <summary>
        /// Uploads local Central changes using SyncProgressDialog (chunked resumable + progress bar).
        /// If a transient failure occurs, offers immediate retry while keeping the local pending marker intact,
        /// preventing the dreaded "Local file incompatible with central model" corruption.
        /// </summary>
        private static bool ExecuteUploadWithProgressAndRetry(
            string projectId,
            string centralPath,
            string user,
            string activeLockToken,
            long? expectedBaseVersion,
            string? expectedBaseHash,
            string localProjectDir)
        {
            string currentToken = activeLockToken;

            while (true)
            {
                try
                {
                    // If retrying after a previous attempt released the lock, acquire a fresh lock first
                    if (string.IsNullOrEmpty(currentToken))
                    {
                        var cfg = AppConfig.Load();
                        var lockTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.AcquireSyncLock,
                            PayloadJson = JsonSerializer.Serialize(new LockRequest
                            {
                                ProjectId = projectId,
                                User = user,
                                Machine = Environment.MachineName,
                                TimeoutSeconds = Math.Max(120, cfg.SyncTimeoutSeconds)
                            })
                        }, timeoutMs: 30000));
                        lockTask.Wait();
                        var lockResp = lockTask.Result.Success && !string.IsNullOrEmpty(lockTask.Result.DataJson)
                            ? JsonSerializer.Deserialize<LockResponse>(lockTask.Result.DataJson)
                            : null;

                        if (lockResp == null || lockResp.Status != SyncLockStatus.LockAcquired || string.IsNullOrEmpty(lockResp.LockToken))
                        {
                            LastUploadFailed = true;
                            LastUploadError = lockResp?.Message ?? lockTask.Result.ErrorMessage ?? "Impossibile riacquisire il lock su Google Drive.";
                            var askBusy = System.Windows.MessageBox.Show(
                                $"Impossibile riprendere l'upload su Google Drive:\n{LastUploadError}\n\nVuoi riprovare adesso? (Scegli 'No' per mantenere il salvataggio locale in attesa di ripresa successiva).",
                                "Ripresa Upload Drive",
                                System.Windows.MessageBoxButton.YesNo,
                                System.Windows.MessageBoxImage.Warning);
                            if (askBusy == System.Windows.MessageBoxResult.Yes) continue;
                            return false;
                        }

                        currentToken = lockResp.LockToken;
                        StartLockHeartbeat(projectId, currentToken, user);
                    }

                    var uploadPayload = JsonSerializer.Serialize(new
                    {
                        ProjectId = projectId,
                        CentralPath = centralPath,
                        LockToken = currentToken,
                        User = user,
                        ExpectedBaseVersion = expectedBaseVersion,
                        ExpectedBaseHash = expectedBaseHash
                    });

                    var uploadDlg = new SyncProgressDialog(
                        $"Google Drive Connector - Upload '{projectId}'",
                        "Caricamento modifiche su Google Drive (chunked resumable)...",
                        new IpcRequest
                        {
                            Command = IpcCommand.UploadProjectChanges,
                            PayloadJson = uploadPayload
                        },
                        timeoutMs: 1800000); // 30 minutes max for 1 GB models on slow uplinks

                    uploadDlg.ShowDialog();
                    var uploadResp = uploadDlg.Response;

                    // LocalServiceEngine releases the lock in its finally block after UploadProjectChanges
                    currentToken = string.Empty;

                    if (uploadResp != null && uploadResp.Success)
                    {
                        LastUploadFailed = false;
                        LastUploadError = null;
                        CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
                        return true;
                    }

                    string err = uploadResp?.ErrorMessage ?? "Errore sconosciuto durante l'upload su Google Drive.";
                    LastUploadFailed = true;
                    LastUploadError = err;

                    // Update marker with last error so next sync knows NOT to overwrite Central!
                    CentralSnapshotHelper.MarkPendingUpload(localProjectDir, new PendingUploadMarker
                    {
                        ProjectId = projectId,
                        CentralPath = centralPath,
                        User = user,
                        ExpectedBaseVersion = expectedBaseVersion,
                        ExpectedBaseHash = expectedBaseHash,
                        CreatedAtUtc = DateTime.UtcNow,
                        LastError = err
                    });

                    bool isConflict = err.Contains("CONFLITTO RILEVATO", StringComparison.OrdinalIgnoreCase);
                    if (isConflict)
                    {
                        System.Windows.MessageBox.Show(
                            $"La sincronizzazione locale in Revit è avvenuta, ma il caricamento su Google Drive è stato bloccato per conflitto:\n\n{err}\n\n" +
                            "Le tue modifiche sono conservate nel modello locale. Salva una copia di backup del file se necessario prima di riallinearti all'ultima versione del team.",
                            "Conflitto Upload Google Drive",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return false;
                    }

                    var retryChoice = System.Windows.MessageBox.Show(
                        $"La sincronizzazione locale in Revit è riuscita, ma il caricamento su Google Drive è stato interrotto:\n\n{err}\n\n" +
                        "IMPORTANTE: Per evitare che il tuo modello locale diventi incompatibile con il centrale, premi 'Sì' per RIPROVARE SUBITO l'upload su Google Drive.",
                        "Upload Drive Interrotto - Riprova",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);

                    if (retryChoice != System.Windows.MessageBoxResult.Yes)
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("SynchronizationCoordinator", $"Eccezione durante upload '{projectId}'", ex);
                    LastUploadFailed = true;
                    LastUploadError = ex.Message;

                    if (!string.IsNullOrEmpty(currentToken))
                    {
                        ReleaseLockSync(projectId, currentToken, user);
                        currentToken = string.Empty;
                    }

                    var retryChoice = System.Windows.MessageBox.Show(
                        $"Errore durante il caricamento su Google Drive:\n{ex.Message}\n\nVuoi riprovare subito l'upload per completare la sincronizzazione in sicurezza?",
                        "Avviso Upload Drive",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);

                    if (retryChoice != System.Windows.MessageBoxResult.Yes)
                    {
                        return false;
                    }
                }
            }
        }

        private static bool HandlePendingLocalUploadRecovery(
            string projectId,
            string centralPath,
            string user,
            bool? canRetryDirectlyHint,
            string? detailMessage)
        {
            string localProjectDir = Path.Combine(AppConfig.Load().LocalCachePath, projectId);
            var pending = CentralSnapshotHelper.ReadPendingUpload(localProjectDir);
            if (pending == null) return true;

            bool canRetry = canRetryDirectlyHint ?? true;
            if (canRetry)
            {
                var choice = System.Windows.MessageBox.Show(
                    $"È stata rilevata una sincronizzazione locale precedente di '{pending.User}' (basata su v{pending.ExpectedBaseVersion}) che non era stata caricata su Google Drive.\n\n" +
                    "• Premi 'Sì' per COMPLETARE ORA l'upload pendente su Google Drive (consigliato: salva tutte le tue modifiche ed evita l'incompatibilità locale/centrale).\n" +
                    "• Premi 'No' per annullare l'operazione senza toccare i file.",
                    "Drive Connector - Upload Pendente Rilevato",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (choice == System.Windows.MessageBoxResult.Yes)
                {
                    try
                    {
                        return ExecuteUploadWithProgressAndRetry(
                            projectId,
                            string.IsNullOrEmpty(pending.CentralPath) ? centralPath : pending.CentralPath,
                            user,
                            string.Empty,
                            pending.ExpectedBaseVersion,
                            pending.ExpectedBaseHash,
                            localProjectDir);
                    }
                    finally
                    {
                        StopLockHeartbeat();
                    }
                }

                return false;
            }
            else
            {
                var choice = System.Windows.MessageBox.Show(
                    $"{detailMessage}\n\n" +
                    "Per non perdere il tuo lavoro:\n" +
                    "1) Usa 'Salva con nome' in Revit per salvare una copia di sicurezza delle tue modifiche.\n" +
                    "2) Poi riapri il progetto da 'Apri da Google Drive' con 'Crea nuovo locale'.\n\n" +
                    "Vuoi resettare lo stato di upload pendente locale ORA per poter scaricare la nuova versione da Google Drive? (Scegli 'No' se devi prima salvare una copia del tuo file).",
                    "Drive Connector - Conflitto Upload Pendente",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (choice == System.Windows.MessageBoxResult.Yes)
                {
                    CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
                    return true;
                }
                return false;
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
        /// Safely executes an operation on the central model (e.g., Relinquish/Checkout):
        /// acquires cloud lock, keeps heartbeat alive through upload, aligns from Drive, reloads local,
        /// executes Revit action, and uploads changes with optimistic concurrency verification.
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
            string localProjectDir = Path.Combine(AppConfig.Load().LocalCachePath, projectId);
            string? lockToken = null;
            bool lockHandledByUpload = false;

            try
            {
                var cfg = AppConfig.Load();
                var lockReq = new LockRequest
                {
                    ProjectId = projectId,
                    User = user,
                    Machine = Environment.MachineName,
                    TimeoutSeconds = Math.Max(120, cfg.SyncTimeoutSeconds)
                };

                var lockTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.AcquireSyncLock,
                    PayloadJson = JsonSerializer.Serialize(lockReq)
                }, timeoutMs: 30000));
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

                var gatekeeper = new SyncGatekeeperDialog(projectId, user);
                bool? gateOk = gatekeeper.ShowDialog();
                if (gateOk != true || !gatekeeper.IsAlignedAndReady)
                {
                    errorMessage = gatekeeper.ErrorMessage ?? "Allineamento da Google Drive annullato o fallito.";
                    return false;
                }

                if (gatekeeper.WasUpdatedFromCloud || reloadIfCloudUpdated)
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

                CentralSnapshotHelper.CreatePreSyncSnapshot(localProjectDir);
                CentralSnapshotHelper.MarkPendingUpload(localProjectDir, new PendingUploadMarker
                {
                    ProjectId = projectId,
                    CentralPath = centralPath,
                    User = user,
                    ExpectedBaseVersion = gatekeeper.CloudVersion,
                    ExpectedBaseHash = gatekeeper.ExpectedHash,
                    CreatedAtUtc = DateTime.UtcNow
                });

                bool shouldUpload = revitAction();
                if (!shouldUpload)
                {
                    CentralSnapshotHelper.ClearPendingUpload(localProjectDir);
                    LiveElementLockService.OnDocumentSynchronized(doc);
                    return true;
                }

                lockHandledByUpload = true;
                bool ok = ExecuteUploadWithProgressAndRetry(
                    projectId,
                    centralPath,
                    user,
                    lockToken,
                    gatekeeper.CloudVersion,
                    gatekeeper.ExpectedHash,
                    localProjectDir);

                if (!ok)
                {
                    errorMessage = LastUploadError ?? "Errore durante il caricamento su Google Drive.";
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
