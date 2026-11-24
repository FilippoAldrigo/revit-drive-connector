using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Core.Services
{
    /// <summary>
    /// 100% Serverless in-process command dispatcher executing directly against Google Drive API v3.
    /// </summary>
    public static class LocalServiceEngine
    {
        public static readonly GoogleDriveService DriveService = new();
        private static bool _initialized = false;
        private static readonly SemaphoreSlim InitLock = new(1, 1);

        public static async Task EnsureInitializedAsync(CancellationToken ct = default)
        {
            if (_initialized) return;
            await InitLock.WaitAsync(ct);
            try
            {
                if (_initialized) return;
                _ = Task.Run(NetworkTimeHelper.SyncTimeOffsetAsync);
                await DriveService.InitializeAsync(ct);
                _initialized = true;
            }
            finally
            {
                InitLock.Release();
            }
        }

        public static async Task<IpcResponse> HandleIpcRequestAsync(
            IpcRequest request,
            CancellationToken ct = default,
            Action<TransferProgressInfo>? progressCallback = null)
        {
            await EnsureInitializedAsync(ct);

            try
            {
                var config = AppConfig.Load();

                switch (request.Command)
                {
                    case IpcCommand.Ping:
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = "PONG"
                        };

                    case IpcCommand.GetDaemonStatus:
                        string? statusProjId = null;
                        if (!string.IsNullOrWhiteSpace(request.PayloadJson))
                        {
                            try
                            {
                                var statusDict = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                                if (statusDict != null && statusDict.TryGetValue("ProjectId", out var pId))
                                {
                                    statusProjId = pId;
                                }
                            }
                            catch { }
                        }

                        FileLockData? activeLock = null;
                        var activeUsers = new List<UserSession>();
                        if (!string.IsNullOrWhiteSpace(statusProjId))
                        {
                            var lockTask = DriveService.GetActiveLockAsync(statusProjId, ct);
                            var usersTask = DriveService.GetActiveUsersAsync(statusProjId, ct);
                            await Task.WhenAll(lockTask, usersTask);
                            activeLock = lockTask.Result;
                            activeUsers = usersTask.Result;
                        }

                        var status = new DaemonStatusInfo
                        {
                            Status = "Online (Serverless API)",
                            LocalCachePath = DriveService.LocalCachePath,
                            GoogleDriveConnected = DriveService.IsNativeGoogleDriveConnected,
                            GoogleAccountEmail = DriveService.ConnectedEmail,
                            Machine = Environment.MachineName,
                            ClockOffsetMs = NetworkTimeHelper.CurrentClockOffset.TotalMilliseconds,
                            IsTimeSynchronized = NetworkTimeHelper.IsTimeSynchronized,
                            CurrentLockHolder = activeLock?.User,
                            CurrentLockMachine = activeLock?.Machine,
                            LockExpiresAtUtc = activeLock?.ExpiresAtUtc,
                            ActiveUsers = activeUsers,
                            TimestampUtc = DateTime.UtcNow
                        };
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(status)
                        };

                    case IpcCommand.NotifyDocumentOpened:
                        var openInfo = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string openProj = openInfo != null && openInfo.TryGetValue("ProjectId", out var op) && !string.IsNullOrEmpty(op)
                            ? op
                            : (openInfo != null && openInfo.TryGetValue("DocumentTitle", out var dt) ? dt : "");
                        string openUser = openInfo != null && openInfo.TryGetValue("User", out var ou) ? ou : "";
                        string openMachine = openInfo != null && openInfo.TryGetValue("Machine", out var om) ? om : Environment.MachineName;

                        if (!string.IsNullOrEmpty(openProj))
                        {
                            await DriveService.UpdateActiveUserSessionAsync(openProj, openUser, openMachine, isActive: true, isSyncing: false, ct);
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = true };

                    case IpcCommand.NotifyDocumentClosed:
                        var closeInfo = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string closeProj = closeInfo != null && closeInfo.TryGetValue("ProjectId", out var cp) && !string.IsNullOrEmpty(cp)
                            ? cp
                            : (closeInfo != null && closeInfo.TryGetValue("DocumentTitle", out var cdt) ? cdt : "");
                        string closeUser = closeInfo != null && closeInfo.TryGetValue("User", out var cu) ? cu : "";
                        string closeMachine = closeInfo != null && closeInfo.TryGetValue("Machine", out var cm) ? cm : Environment.MachineName;

                        if (!string.IsNullOrEmpty(closeProj))
                        {
                            await DriveService.UpdateActiveUserSessionAsync(closeProj, closeUser, closeMachine, isActive: false, isSyncing: false, ct);
                            await DriveService.PublishLiveEditsAsync(closeProj, closeUser, closeMachine, new List<string>(), ct);
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = true };

                    case IpcCommand.ListCloudProjects:
                        var projects = await DriveService.ListProjectsAsync(ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(projects)
                        };

                    case IpcCommand.GetGoogleAccountStatus:
                        var acctInfo = await DriveService.GetAccountInfoAsync(ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(acctInfo)
                        };

                    case IpcCommand.ConnectGoogleAccount:
                        var connectedAcct = await DriveService.ConnectAccountAsync(ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = connectedAcct.IsConnected,
                            ErrorMessage = connectedAcct.IsConnected ? null : connectedAcct.StatusMessage,
                            DataJson = JsonSerializer.Serialize(connectedAcct)
                        };

                    case IpcCommand.DisconnectGoogleAccount:
                        var disconnectedAcct = DriveService.DisconnectAccount();
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(disconnectedAcct)
                        };

                    case IpcCommand.SaveGoogleCredentials:
                        bool saved = await DriveService.SaveCredentialsAsync(request.PayloadJson, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = saved,
                            ErrorMessage = saved ? null : "Impossibile salvare il file credentials.json",
                            DataJson = saved ? "Credenziali salvate con successo." : null
                        };

                    case IpcCommand.MountCloudProject:
                        var mountReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string mProjectName = mountReq?["ProjectName"] ?? "";
                        string mFolderId = mountReq?["CloudFolderId"] ?? "";
                        string mCentralPath = await DriveService.MountCloudProjectAsync(mProjectName, mFolderId, ct, progressCallback);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["CentralPath"] = mCentralPath })
                        };

                    case IpcCommand.AddCollaborator:
                        var addCollabReq = JsonSerializer.Deserialize<AddCollaboratorRequest>(request.PayloadJson);
                        if (addCollabReq != null)
                        {
                            var addResult = await DriveService.AddCollaboratorAsync(
                                addCollabReq.ProjectName,
                                addCollabReq.Email,
                                addCollabReq.Role,
                                addCollabReq.SendNotificationEmail,
                                ct);
                            return new IpcResponse
                            {
                                RequestId = request.RequestId,
                                Success = addResult.Success,
                                ErrorMessage = addResult.Success ? null : addResult.Message,
                                DataJson = JsonSerializer.Serialize(addResult)
                            };
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = false, ErrorMessage = "Payload AddCollaborator non valido." };

                    case IpcCommand.ListCollaborators:
                        var listReqDict = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string lProjectName = listReqDict?["ProjectName"] ?? "";
                        var collaborators = await DriveService.ListCollaboratorsAsync(lProjectName, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(collaborators)
                        };

                    case IpcCommand.RemoveCollaborator:
                        var remCollabReq = JsonSerializer.Deserialize<RemoveCollaboratorRequest>(request.PayloadJson);
                        if (remCollabReq != null)
                        {
                            var remResult = await DriveService.RemoveCollaboratorAsync(remCollabReq.ProjectName, remCollabReq.PermissionId, ct);
                            return new IpcResponse
                            {
                                RequestId = request.RequestId,
                                Success = remResult.Success,
                                ErrorMessage = remResult.Success ? null : remResult.Message,
                                DataJson = JsonSerializer.Serialize(remResult)
                            };
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = false, ErrorMessage = "Payload RemoveCollaborator non valido." };

                    case IpcCommand.DownloadAndPrepareProject:
                        var prepReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string projectName = prepReq != null && prepReq.TryGetValue("ProjectName", out var pnVal) ? pnVal : "";
                        string? prefFolderId = prepReq != null && prepReq.TryGetValue("CloudFolderId", out var cfVal) && !string.IsNullOrWhiteSpace(cfVal) ? cfVal : null;
                        string centralPath = await DriveService.DownloadAndPrepareProjectAsync(projectName, prefFolderId, ct, progressCallback);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["CentralPath"] = centralPath })
                        };

                    case IpcCommand.AcquireSyncLock:
                        var lockReq = JsonSerializer.Deserialize<LockRequest>(request.PayloadJson);
                        if (lockReq == null)
                        {
                            return new IpcResponse { RequestId = request.RequestId, Success = false, ErrorMessage = "Dati lock non validi" };
                        }

                        var driveLockResp = await DriveService.AcquireProjectLockAsync(lockReq, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = driveLockResp.Status != SyncLockStatus.Error,
                            ErrorMessage = driveLockResp.Status == SyncLockStatus.Error ? driveLockResp.Message : null,
                            DataJson = JsonSerializer.Serialize(driveLockResp)
                        };

                    case IpcCommand.HeartbeatSyncLock:
                        var hbReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string hbProj = hbReq != null && hbReq.TryGetValue("ProjectId", out var hp) ? hp : "";
                        string hbToken = hbReq != null && hbReq.TryGetValue("LockToken", out var ht) ? ht : "";
                        string hbUser = hbReq != null && hbReq.TryGetValue("User", out var hu) ? hu : "";
                        bool hbOk = await DriveService.HeartbeatProjectLockAsync(hbProj, hbToken, hbUser, config.SyncTimeoutSeconds, ct);
                        return new IpcResponse { RequestId = request.RequestId, Success = hbOk };

                    case IpcCommand.ReleaseSyncLock:
                    case IpcCommand.UploadProjectChanges:
                        string upProj = "";
                        string upCentral = "";
                        string token = "";
                        string user = "";
                        long? expectedBaseVersion = null;
                        string? expectedBaseHash = null;

                        using (var payloadDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.PayloadJson) ? "{}" : request.PayloadJson))
                        {
                            var root = payloadDoc.RootElement;
                            if (root.TryGetProperty("ProjectId", out var pEl) && pEl.ValueKind == JsonValueKind.String) upProj = pEl.GetString() ?? "";
                            if (root.TryGetProperty("CentralPath", out var cEl) && cEl.ValueKind == JsonValueKind.String) upCentral = cEl.GetString() ?? "";
                            if (root.TryGetProperty("LockToken", out var tEl) && tEl.ValueKind == JsonValueKind.String) token = tEl.GetString() ?? "";
                            if (root.TryGetProperty("User", out var uEl) && uEl.ValueKind == JsonValueKind.String) user = uEl.GetString() ?? "";
                            if (root.TryGetProperty("ExpectedBaseVersion", out var vEl) && vEl.ValueKind == JsonValueKind.Number && vEl.TryGetInt64(out var parsedVer))
                                expectedBaseVersion = parsedVer;
                            if (root.TryGetProperty("ExpectedBaseHash", out var hEl) && hEl.ValueKind == JsonValueKind.String)
                                expectedBaseHash = hEl.GetString();
                        }

                        if (request.Command == IpcCommand.UploadProjectChanges && !string.IsNullOrEmpty(upCentral))
                        {
                            try
                            {
                                var (lockValid, lockReason) = await DriveService.VerifyLockOwnershipAsync(upProj, token, user, ct);
                                if (!lockValid)
                                {
                                    return new IpcResponse
                                    {
                                        RequestId = request.RequestId,
                                        Success = false,
                                        ErrorMessage = lockReason
                                    };
                                }

                                await DriveService.UploadProjectChangesAsync(
                                    upProj,
                                    upCentral,
                                    user,
                                    expectedBaseVersion,
                                    expectedBaseHash,
                                    isInitialCreate: false,
                                    ct: ct,
                                    progressCallback: progressCallback);
                            }
                            finally
                            {
                                if (!string.IsNullOrWhiteSpace(token))
                                {
                                    try { await DriveService.ReleaseProjectLockAsync(upProj, token, user, CancellationToken.None); } catch { }
                                }
                            }
                        }
                        else
                        {
                            await DriveService.ReleaseProjectLockAsync(upProj, token, user, ct);
                        }

                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = "Modifiche caricate e lock rilasciato."
                        };

                    case IpcCommand.UploadProjectLinks:
                        var linksReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string lnkProj = linksReq != null && linksReq.TryGetValue("ProjectId", out var lpVal) ? lpVal : "";
                        string lnkDir = linksReq != null && linksReq.TryGetValue("LinksDir", out var ldVal) ? ldVal : "";
                        if (!string.IsNullOrEmpty(lnkProj) && !string.IsNullOrEmpty(lnkDir))
                        {
                            await DriveService.UploadProjectLinksAsync(lnkProj, lnkDir, ct);
                        }
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = "Collegamenti esterni caricati su Google Drive."
                        };

                    case IpcCommand.CreateCloudProject:
                        var createReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string newProj = createReq?["ProjectName"] ?? "";
                        string newCentral = createReq?["CentralPath"] ?? "";
                        string newUser = createReq?["User"] ?? "";

                        await DriveService.CreateCloudProjectAsync(newProj, newCentral, newUser, ct, progressCallback);
                        await DriveService.UpdateActiveUserSessionAsync(newProj, newUser, Environment.MachineName, isActive: true, isSyncing: false, ct);

                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = "Progetto creato e pubblicato su cloud con successo."
                        };

                    case IpcCommand.SendBorrowRequest:
                        var bReq = JsonSerializer.Deserialize<BorrowRequest>(request.PayloadJson);
                        if (bReq != null)
                        {
                            string pDir = await DriveService.SyncBorrowRequestsFileAsync(bReq.ProjectId, ct);
                            BorrowRequestManager.SendRequest(pDir, bReq);
                            await DriveService.PushBorrowRequestsFileAsync(bReq.ProjectId, pDir, ct);
                            return new IpcResponse { RequestId = request.RequestId, Success = true, DataJson = JsonSerializer.Serialize(bReq) };
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = false, ErrorMessage = "Dati richiesta prestito nulli" };

                    case IpcCommand.GetPendingBorrowRequests:
                        var pUserReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string pProj = pUserReq?["ProjectId"] ?? "";
                        string pUser = pUserReq?["User"] ?? "";
                        string projLocalDir = await DriveService.SyncBorrowRequestsFileAsync(pProj, ct);
                        var pending = BorrowRequestManager.GetPendingRequestsForUser(projLocalDir, pUser);
                        return new IpcResponse { RequestId = request.RequestId, Success = true, DataJson = JsonSerializer.Serialize(pending) };

                    case IpcCommand.GetOutgoingBorrowRequests:
                        var outReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string outProj = outReq != null && outReq.TryGetValue("ProjectId", out var opOut) ? opOut : "";
                        string outUser = outReq != null && outReq.TryGetValue("User", out var ouOut) ? ouOut : "";
                        bool skipPull = outReq != null && outReq.TryGetValue("SkipCloudPull", out var spVal) && string.Equals(spVal, "true", StringComparison.OrdinalIgnoreCase);
                        string outLocalDir = skipPull
                            ? DriveService.GetLocalProjectDir(outProj)
                            : await DriveService.SyncBorrowRequestsFileAsync(outProj, ct);
                        var outgoing = BorrowRequestManager.GetOutgoingRequestsForUser(outLocalDir, outUser);
                        return new IpcResponse { RequestId = request.RequestId, Success = true, DataJson = JsonSerializer.Serialize(outgoing) };

                    case IpcCommand.RespondToBorrowRequest:
                        var respDto = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string rProj = respDto?["ProjectId"] ?? "";
                        string rId = respDto?["RequestId"] ?? "";
                        string rStatus = respDto?["Status"] ?? "Granted";
                        string rUser = respDto?["User"] ?? "";
                        var statusEnum = rStatus == "Granted" ? BorrowRequestStatus.Granted : BorrowRequestStatus.Denied;

                        string rLocalDir = await DriveService.SyncBorrowRequestsFileAsync(rProj, ct);
                        bool ok = BorrowRequestManager.RespondToRequest(rLocalDir, rId, statusEnum, rUser);
                        if (ok)
                        {
                            await DriveService.PushBorrowRequestsFileAsync(rProj, rLocalDir, ct);
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = ok };

                    case IpcCommand.MarkBorrowRequestCompleted:
                        var compDto = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string cProj = compDto?["ProjectId"] ?? "";
                        string cIdsRaw = compDto?["RequestIds"] ?? "";
                        var cIds = cIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        string cLocalDir = await DriveService.SyncBorrowRequestsFileAsync(cProj, ct);
                        bool compOk = BorrowRequestManager.MarkRequestsCompleted(cLocalDir, cIds);
                        if (compOk)
                        {
                            await DriveService.PushBorrowRequestsFileAsync(cProj, cLocalDir, ct);
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = compOk };

                    case IpcCommand.VerifySyncIntegrity:
                        var intReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string intProj = intReq?["ProjectId"] ?? "";
                        string intUser = intReq?["User"] ?? "";

                        var integrityResult = await DriveService.VerifyIntegrityAndAlignAsync(intProj, intUser, ct, progressCallback);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(integrityResult)
                        };

                    case IpcCommand.ForceBreakLock:
                        var breakReq = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string breakProj = breakReq?["ProjectId"] ?? "";
                        bool bSuccess = await DriveService.ForceBreakProjectLockAsync(breakProj, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = bSuccess,
                            DataJson = bSuccess ? "Lock rimosso con successo." : "Impossibile rimuovere il lock su Google Drive."
                        };

                    case IpcCommand.PublishLiveEdits:
                        var liveState = JsonSerializer.Deserialize<UserLiveEditsState>(request.PayloadJson);
                        if (liveState != null && !string.IsNullOrWhiteSpace(liveState.ProjectId) && !string.IsNullOrWhiteSpace(liveState.User))
                        {
                            string machineName = string.IsNullOrWhiteSpace(liveState.Machine) ? Environment.MachineName : liveState.Machine;
                            await DriveService.PublishLiveEditsAsync(
                                liveState.ProjectId,
                                liveState.User,
                                machineName,
                                liveState.ElementUniqueIds ?? new List<string>(),
                                ct);
                        }
                        return new IpcResponse { RequestId = request.RequestId, Success = true };

                    case IpcCommand.GetRemoteLiveEdits:
                        var liveQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string lqProj = liveQuery != null && liveQuery.TryGetValue("ProjectId", out var lp) ? lp : "";
                        string lqUser = liveQuery != null && liveQuery.TryGetValue("User", out var lu) ? lu : "";
                        string lqMachine = liveQuery != null && liveQuery.TryGetValue("Machine", out var lm) ? lm : Environment.MachineName;
                        var remoteLocks = await DriveService.GetRemoteLiveEditsAsync(lqProj, lqUser, lqMachine, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(remoteLocks)
                        };

                    case IpcCommand.PollConnectorState:
                        var pollQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string pollProj = pollQuery != null && pollQuery.TryGetValue("ProjectId", out var pp) ? pp : "";
                        string pollUser = pollQuery != null && pollQuery.TryGetValue("User", out var pu) ? pu : "";
                        string pollMachine = pollQuery != null && pollQuery.TryGetValue("Machine", out var pm) ? pm : Environment.MachineName;
                        var pollSnapshot = await DriveService.PollConnectorStateAsync(pollProj, pollUser, pollMachine, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(pollSnapshot)
                        };

                    case IpcCommand.PreflightCheck:
                        var preReq = !string.IsNullOrWhiteSpace(request.PayloadJson)
                            ? JsonSerializer.Deserialize<PreflightCheckRequest>(request.PayloadJson)
                            : new PreflightCheckRequest();

                        long minBytes = preReq?.MinRequiredFreeBytes ?? (2L * 1024 * 1024 * 1024);
                        var (diskOk, freeBytes, totalBytes, driveName) = DiskCleanupService.CheckDiskSpace(DriveService.LocalCachePath, minBytes);

                        var authInfo = await DriveService.ValidateCurrentCredentialAsync(ct);
                        bool authOk = authInfo != null && authInfo.IsConnected;

                        string? preflightError = null;
                        if (!authOk)
                        {
                            preflightError = "Account Google non autenticato o token revocato. Effettua l'accesso da 'Account Google' nel Ribbon prima di sincronizzare.";
                        }
                        else if (!diskOk)
                        {
                            preflightError = $"Spazio su disco insufficiente sull'unità {driveName} ({freeBytes / (1024.0 * 1024.0):F0} MB liberi). Richiesti almeno {minBytes / (1024.0 * 1024.0):F0} MB per garantire l'integrità del modello.";
                        }

                        var preResult = new PreflightCheckResult
                        {
                            Success = authOk && diskOk,
                            IsAuthValid = authOk,
                            AuthEmail = authInfo?.Email ?? "",
                            IsDiskSpaceSufficient = diskOk,
                            AvailableFreeBytes = freeBytes,
                            RequiredFreeBytes = minBytes,
                            DriveLetter = driveName,
                            ErrorMessage = preflightError
                        };
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = preResult.Success,
                            ErrorMessage = preflightError,
                            DataJson = JsonSerializer.Serialize(preResult)
                        };

                    case IpcCommand.GetVersionHistory:
                        var histQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string histProj = histQuery != null && histQuery.TryGetValue("ProjectId", out var histPid) ? histPid : "";
                        var historyResp = await DriveService.GetProjectHistoryAsync(histProj, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(historyResp)
                        };

                    case IpcCommand.RollbackProjectVersion:
                        var rbReq = JsonSerializer.Deserialize<RollbackRequest>(request.PayloadJson);
                        if (rbReq == null || string.IsNullOrWhiteSpace(rbReq.ProjectId) || string.IsNullOrWhiteSpace(rbReq.RevisionId))
                        {
                            throw new ArgumentException("Parametri non validi per la richiesta di rollback.");
                        }
                        var rbResp = await DriveService.RollbackProjectAsync(
                            rbReq.ProjectId,
                            rbReq.RevisionId,
                            rbReq.User,
                            rbReq.Machine,
                            rbReq.Reason,
                            ct,
                            progressCallback);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = rbResp.Success,
                            DataJson = JsonSerializer.Serialize(rbResp)
                        };

                    case IpcCommand.CheckCloudLinkUpdates:
                        var linksQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string lProj = linksQuery != null && linksQuery.TryGetValue("ProjectId", out var lnkPid) ? lnkPid : "";
                        string lLocalDir = linksQuery != null && linksQuery.TryGetValue("LocalProjectDir", out var lld) ? lld : Path.Combine(DriveService.LocalCachePath, lProj);
                        var linksResp = await DriveService.CheckCloudLinksAsync(lProj, lLocalDir, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(linksResp)
                        };

                    case IpcCommand.DownloadCloudLink:
                        var dlQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string dlProj = dlQuery != null && dlQuery.TryGetValue("ProjectId", out var dlp) ? dlp : "";
                        string dlFile = dlQuery != null && dlQuery.TryGetValue("FileName", out var dlf) ? dlf : "";
                        string dlLocalDir = dlQuery != null && dlQuery.TryGetValue("LocalProjectDir", out var dlld) ? dlld : Path.Combine(DriveService.LocalCachePath, dlProj);
                        bool dlOk = await DriveService.DownloadCloudLinkAsync(dlProj, dlFile, dlLocalDir, ct, progressCallback);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = dlOk,
                            ErrorMessage = dlOk ? null : $"Impossibile scaricare il collegamento '{dlFile}' da Google Drive."
                        };

                    case IpcCommand.RunDiskCleanup:
                        var cleanRes = DriveService.PerformIntelligentCleanup();
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(cleanRes)
                        };

                    case IpcCommand.ExportDiagnostics:
                        var expQuery = JsonSerializer.Deserialize<Dictionary<string, string>>(request.PayloadJson);
                        string expPath = expQuery != null && expQuery.TryGetValue("DestinationZipPath", out var ep)
                            ? ep
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"RevitDriveConnector_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
                        var expRes = await DriveService.ExportDiagnosticsZipAsync(expPath, ct);
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = true,
                            DataJson = JsonSerializer.Serialize(expRes)
                        };

                    default:
                        return new IpcResponse
                        {
                            RequestId = request.RequestId,
                            Success = false,
                            ErrorMessage = $"Comando non supportato: {request.Command}"
                        };
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("LocalServiceEngine", $"Errore gestione comando {request.Command}", ex);
                return new IpcResponse
                {
                    RequestId = request.RequestId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
    }
}
