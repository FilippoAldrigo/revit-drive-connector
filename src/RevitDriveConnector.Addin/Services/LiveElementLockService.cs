using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Services;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Addin.Services
{
    /// <summary>
    /// Dynamic Model Updater (IUpdater) that blocks modifying or deleting elements currently
    /// live-locked (edited but not yet synced) by another teammate.
    /// </summary>
    public class LiveElementLockUpdater : IUpdater
    {
        private readonly UpdaterId _updaterId;
        private readonly FailureDefinitionId _failureId;

        public LiveElementLockUpdater(AddInId addInId)
        {
            _updaterId = new UpdaterId(addInId, new Guid("7E2A9B14-5C6D-4E8B-9F1A-3B2C4D5E6F70"));
            _failureId = new FailureDefinitionId(new Guid("A4D81F23-9E0B-4C7A-8B12-6F5E4D3C2B10"));
        }

        public FailureDefinitionId FailureDefId => _failureId;

        public void Execute(UpdaterData data)
        {
            if (!LiveElementLockService.IsLiveLockEnabled || LiveElementLockService.IsSyncOrReloadActive)
            {
                return;
            }

            var doc = data.GetDocument();
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            if (!LiveElementLockService.HasAnyRemoteLocks(projectId)) return;

            var modifiedIds = data.GetModifiedElementIds();
            var deletedIds = data.GetDeletedElementIds();
            if (modifiedIds.Count == 0 && deletedIds.Count == 0) return;

            foreach (var elemId in modifiedIds)
            {
                var elem = doc.GetElement(elemId);
                if (elem == null || !LiveElementLockService.ShouldTrackElement(elem)) continue;

                // Strict join-cascade filter: only skip if another element is actively selected (<2s ago)
                // AND multiple elements changed in the same transaction AND this element is NOT selected.
                if (modifiedIds.Count > 1 && LiveElementLockService.IsStrictJoinCascade(projectId, elem, modifiedIds))
                {
                    continue;
                }

                if (LiveElementLockService.TryGetRemoteLock(projectId, elem.UniqueId, out var lockInfo) && lockInfo != null)
                {
                    var fm = new FailureMessage(_failureId);
                    fm.SetFailingElement(elemId);
                    doc.PostFailure(fm);
                    LiveElementLockService.NotifyConflictToast(lockInfo, isWarningOnly: false);
                    return;
                }
            }

            foreach (var delId in deletedIds)
            {
                if (LiveElementLockService.TryGetCachedUniqueId(projectId, delId, out string? uid) &&
                    !string.IsNullOrEmpty(uid) &&
                    LiveElementLockService.TryGetRemoteLock(projectId, uid, out var lockInfo) && lockInfo != null)
                {
                    var fm = new FailureMessage(_failureId);
                    fm.SetFailingElement(delId);
                    doc.PostFailure(fm);
                    LiveElementLockService.NotifyConflictToast(lockInfo, isWarningOnly: false);
                    return;
                }
            }
        }

        public string GetAdditionalInformation() => "Prevents editing or deleting elements currently modified live by teammates before sync.";
        public ChangePriority GetChangePriority() => ChangePriority.FloorsRoofsStructuralWalls;
        public UpdaterId GetUpdaterId() => _updaterId;
        public string GetUpdaterName() => "RevitDriveConnector_LiveElementLockUpdater";
    }

    public static class LiveElementLockService
    {
        private sealed class ProjectLiveState
        {
            public readonly object SyncRoot = new();
            public string ProjectId { get; init; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public HashSet<string> LocalDirty { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SessionAdded { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<string, RemoteElementLockInfo> RemoteLocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<long, string> ElementIdMap { get; } = new();
            public HashSet<long> RecentSelection { get; set; } = new();
            public DateTime LastSelectionUtc { get; set; } = DateTime.MinValue;
            public DateTime LastLocalPushUtc { get; set; } = DateTime.MinValue;
            public DateTime LastEditActivityUtc { get; set; } = DateTime.MinValue;
            public DateTime LastRemotePollUtc { get; set; } = DateTime.MinValue;
            public bool HasPendingLocalPush { get; set; }
            public int IoBusyFlag; // 0 = idle, 1 = busy (Interlocked)
        }

        private static readonly NamedPipeIpcClient IpcClient = new();
        private static readonly ConcurrentDictionary<string, ProjectLiveState> StatesByProject = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<BuiltInCategory> ExcludedCategories = new()
        {
            BuiltInCategory.OST_Views,
            BuiltInCategory.OST_Sheets,
            BuiltInCategory.OST_ProjectInformation,
            BuiltInCategory.OST_Materials,
            BuiltInCategory.OST_Cameras,
            BuiltInCategory.OST_SketchLines,
            BuiltInCategory.OST_ScheduleGraphics,
            BuiltInCategory.OST_PropertySet,
            BuiltInCategory.OST_PreviewLegendComponents,
            BuiltInCategory.OST_SunStudy,
            BuiltInCategory.OST_Dimensions,
            BuiltInCategory.OST_Constraints,
            BuiltInCategory.OST_WeakDims,
            BuiltInCategory.OST_Rooms,
            BuiltInCategory.OST_RoomTags,
            BuiltInCategory.OST_RoomSeparationLines,
            BuiltInCategory.OST_MEPSpaces,
            BuiltInCategory.OST_Areas,
            BuiltInCategory.OST_AreaTags,
            BuiltInCategory.OST_ReferencePoints,
            BuiltInCategory.OST_CLines,
            BuiltInCategory.OST_CenterLines,
            BuiltInCategory.OST_CurtainGrids,
            BuiltInCategory.OST_DividedSurface
        };

        private static readonly HashSet<BuiltInCategory> JoinCascadingCategories = new()
        {
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Roofs,
            BuiltInCategory.OST_Ceilings,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves
        };

        private static LiveElementLockUpdater? _updater;
        private static System.Threading.Timer? _backgroundPoller;
        private static volatile bool _isSyncOrReloadActive = false;
        private static volatile bool _isLiveLockEnabled = true;
        private static DateTime _lastToastShownUtc = DateTime.MinValue;

        public static bool IsLiveLockEnabled => _isLiveLockEnabled;

        public static bool IsSyncOrReloadActive
        {
            get => _isSyncOrReloadActive;
            set => _isSyncOrReloadActive = value;
        }

        public static void ReloadSettings()
        {
            try
            {
                _isLiveLockEnabled = AppConfig.Load().EnableLiveElementLocks;
            }
            catch
            {
                _isLiveLockEnabled = true;
            }
        }

        public static void Initialize(UIControlledApplication application)
        {
            ReloadSettings();

            try
            {
                _updater = new LiveElementLockUpdater(application.ActiveAddInId);

                if (!FailureDefinitionRegistryContains(_updater.FailureDefId))
                {
                    FailureDefinition.CreateFailureDefinition(
                        _updater.FailureDefId,
                        FailureSeverity.Error,
                        Loc.Tr("LiveLock_FailureStatic"));
                }

                if (!UpdaterRegistry.IsUpdaterRegistered(_updater.GetUpdaterId()))
                {
                    UpdaterRegistry.RegisterUpdater(_updater, isOptional: true);
                    var instanceFilter = new ElementIsElementTypeFilter(inverted: true);
                    UpdaterRegistry.AddTrigger(_updater.GetUpdaterId(), instanceFilter, Element.GetChangeTypeAny());
                    UpdaterRegistry.AddTrigger(_updater.GetUpdaterId(), instanceFilter, Element.GetChangeTypeElementDeletion());
                }

                // Start background unified poller (runs every 4s independent of Revit OnIdling!)
                _backgroundPoller?.Dispose();
                _backgroundPoller = new System.Threading.Timer(BackgroundPollCallback, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("LiveElementLockService", "Initialize updater warning", ex);
            }
        }

        public static void Shutdown()
        {
            try
            {
                _backgroundPoller?.Dispose();
                _backgroundPoller = null;

                if (_updater != null && UpdaterRegistry.IsUpdaterRegistered(_updater.GetUpdaterId()))
                {
                    UpdaterRegistry.UnregisterUpdater(_updater.GetUpdaterId());
                }
            }
            catch { }
        }

        private static bool FailureDefinitionRegistryContains(FailureDefinitionId id)
        {
            try
            {
                var reg = Autodesk.Revit.ApplicationServices.Application.GetFailureDefinitionRegistry();
                return reg.FindFailureDefinition(id) != null;
            }
            catch
            {
                return false;
            }
        }

        private static ProjectLiveState GetOrCreateState(string projectId, string? username = null)
        {
            var state = StatesByProject.GetOrAdd(projectId, pid => new ProjectLiveState { ProjectId = pid });
            if (!string.IsNullOrWhiteSpace(username))
            {
                state.Username = username;
            }
            return state;
        }

        public static bool HasAnyRemoteLocks(string projectId)
        {
            return StatesByProject.TryGetValue(projectId, out var state) && !state.RemoteLocks.IsEmpty;
        }

        public static (Dictionary<string, RemoteElementLockInfo> remoteLocks, HashSet<string> localDirty) GetProjectLockSnapshots(string projectId)
        {
            if (StatesByProject.TryGetValue(projectId, out var state))
            {
                Dictionary<string, RemoteElementLockInfo> rem;
                HashSet<string> loc;
                lock (state.SyncRoot)
                {
                    rem = new Dictionary<string, RemoteElementLockInfo>(state.RemoteLocks, StringComparer.OrdinalIgnoreCase);
                    loc = new HashSet<string>(state.LocalDirty, StringComparer.OrdinalIgnoreCase);
                }
                return (rem, loc);
            }
            return (new Dictionary<string, RemoteElementLockInfo>(), new HashSet<string>());
        }

        public static void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            if (!_isLiveLockEnabled || _isSyncOrReloadActive) return;

            try
            {
                var doc = e.GetDocument();
                if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

                string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
                if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

                string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
                var state = GetOrCreateState(projectId, doc.Application.Username);
                var selected = e.GetSelectedElements();

                var set = new HashSet<long>();
                RemoteElementLockInfo? selectedLockedInfo = null;

                if (selected != null)
                {
                    foreach (var id in selected)
                    {
                        set.Add(id.Value);
                        var elem = doc.GetElement(id);
                        if (elem != null && !string.IsNullOrEmpty(elem.UniqueId))
                        {
                            // Lazily populate ElementId -> UniqueId cache so deletions are always tracked
                            state.ElementIdMap[id.Value] = elem.UniqueId;

                            if (selectedLockedInfo == null &&
                                ShouldTrackElement(elem) &&
                                state.RemoteLocks.TryGetValue(elem.UniqueId, out var lockInfo))
                            {
                                selectedLockedInfo = lockInfo;
                            }
                        }
                    }
                }

                lock (state.SyncRoot)
                {
                    state.RecentSelection = set;
                    state.LastSelectionUtc = DateTime.UtcNow;
                }

                // Proactive warning toast the moment a user selects a teammate's live-locked element!
                if (selectedLockedInfo != null)
                {
                    NotifyConflictToast(selectedLockedInfo, isWarningOnly: true);
                }
            }
            catch { }
        }

        /// <summary>
        /// Strict join-cascade detection: only true when selection happened within 2.5 seconds,
        /// the element itself is NOT selected, AND at least one selected element is also in modifiedIds.
        /// Prevents false negatives when users modify walls/elements directly via Trim/Move/Properties.
        /// </summary>
        public static bool IsStrictJoinCascade(string projectId, Element elem, ICollection<ElementId> modifiedIds)
        {
            if (elem?.Category == null) return false;
            if (!Enum.IsDefined(typeof(BuiltInCategory), elem.Category.Id.Value)) return false;
            var bic = (BuiltInCategory)elem.Category.Id.Value;
            if (!JoinCascadingCategories.Contains(bic)) return false;

            if (!StatesByProject.TryGetValue(projectId, out var state)) return false;

            HashSet<long> selectedSet;
            DateTime lastSelUtc;
            lock (state.SyncRoot)
            {
                selectedSet = state.RecentSelection;
                lastSelUtc = state.LastSelectionUtc;
            }

            if (selectedSet.Count == 0 || (DateTime.UtcNow - lastSelUtc).TotalSeconds > 2.5)
            {
                return false;
            }

            if (selectedSet.Contains(elem.Id.Value))
            {
                return false;
            }

            return modifiedIds.Any(id => selectedSet.Contains(id.Value));
        }

        public static void OnDocumentOpened(Document doc)
        {
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            var state = GetOrCreateState(projectId, doc.Application.Username);
            state.LastRemotePollUtc = DateTime.MinValue;

            // Lightweight seeding of active view elements only (avoids multi-second full-model scan on 1 GB projects!)
            if (_isLiveLockEnabled)
            {
                SeedActiveViewLookupCache(doc, state);
                TriggerStateSyncAsync(state, forcePoll: true);
            }
        }

        public static void SeedElementLookupCache(Document doc, string projectId)
        {
            if (!_isLiveLockEnabled || doc == null) return;
            var state = GetOrCreateState(projectId, doc.Application.Username);
            SeedActiveViewLookupCache(doc, state);
        }

        private static void SeedActiveViewLookupCache(Document doc, ProjectLiveState state)
        {
            try
            {
                var activeView = doc.ActiveView;
                if (activeView == null) return;

                var collector = new FilteredElementCollector(doc, activeView.Id).WhereElementIsNotElementType();
                foreach (var elem in collector)
                {
                    if (elem.Id != null && !string.IsNullOrEmpty(elem.UniqueId) && ShouldTrackElement(elem))
                    {
                        state.ElementIdMap[elem.Id.Value] = elem.UniqueId;
                    }
                }
            }
            catch { }
        }

        public static void OnDocumentChanged(DocumentChangedEventArgs e)
        {
            if (!_isLiveLockEnabled || _isSyncOrReloadActive) return;

            var doc = e.GetDocument();
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            if (e.Operation == UndoOperation.TransactionGroupRolledBack) return;

            var txNames = e.GetTransactionNames();
            if (txNames != null && txNames.Any(t =>
                t.Contains("Synchronize", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Reload", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Relinquish", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Drive", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Consolida", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Auto-Risoluzione", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            var state = GetOrCreateState(projectId, doc.Application.Username);
            bool changed = false;

            var modifiedIds = e.GetModifiedElementIds();
            var addedIds = e.GetAddedElementIds();
            var deletedIds = e.GetDeletedElementIds();

            lock (state.SyncRoot)
            {
                foreach (var id in addedIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;
                    state.ElementIdMap[id.Value] = elem.UniqueId;

                    if (!ShouldTrackElement(elem)) continue;

                    if (e.Operation == UndoOperation.TransactionUndone)
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (state.LocalDirty.Remove(elem.UniqueId)) changed = true;
                            state.SessionAdded.Remove(elem.UniqueId);
                        }
                    }
                    else
                    {
                        state.SessionAdded.Add(elem.UniqueId);
                        if (state.LocalDirty.Add(elem.UniqueId)) changed = true;
                    }
                }

                foreach (var id in modifiedIds)
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;
                    state.ElementIdMap[id.Value] = elem.UniqueId;

                    if (!ShouldTrackElement(elem)) continue;

                    var checkoutStatus = WorksharingUtils.GetCheckoutStatus(doc, id);
                    if (e.Operation == UndoOperation.TransactionUndone)
                    {
                        if (checkoutStatus != CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (state.LocalDirty.Remove(elem.UniqueId)) changed = true;
                        }
                    }
                    else
                    {
                        if (modifiedIds.Count > 1 && IsStrictJoinCascade(projectId, elem, modifiedIds))
                        {
                            continue;
                        }

                        if (checkoutStatus == CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (state.LocalDirty.Add(elem.UniqueId)) changed = true;
                        }
                    }
                }

                foreach (var id in deletedIds)
                {
                    if (state.ElementIdMap.TryGetValue(id.Value, out string? uid) && !string.IsNullOrEmpty(uid))
                    {
                        if (e.Operation == UndoOperation.TransactionUndone)
                        {
                            if (state.LocalDirty.Remove(uid)) changed = true;
                        }
                        else if (state.SessionAdded.Remove(uid))
                        {
                            if (state.LocalDirty.Remove(uid)) changed = true;
                        }
                        else
                        {
                            if (state.LocalDirty.Add(uid)) changed = true;
                        }
                    }
                }

                if (changed)
                {
                    state.HasPendingLocalPush = true;
                    state.LastEditActivityUtc = DateTime.UtcNow;
                }
            }
        }

        public static void OnDocumentSynchronized(Document doc)
        {
            if (doc == null || !doc.IsWorkshared) return;
            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string username = doc.Application.Username;
            var state = GetOrCreateState(projectId, username);

            List<string> remainingOwned;
            lock (state.SyncRoot)
            {
                state.SessionAdded.Clear();
                var toRemove = new List<string>();
                foreach (var uid in state.LocalDirty)
                {
                    var elem = doc.GetElement(uid);
                    if (elem == null || WorksharingUtils.GetCheckoutStatus(doc, elem.Id) != CheckoutStatus.OwnedByCurrentUser)
                    {
                        toRemove.Add(uid);
                    }
                }

                foreach (var uid in toRemove) state.LocalDirty.Remove(uid);
                remainingOwned = state.LocalDirty.ToList();
                state.HasPendingLocalPush = false;
                state.LastLocalPushUtc = DateTime.UtcNow;
                state.LastRemotePollUtc = DateTime.MinValue;
            }

            SeedActiveViewLookupCache(doc, state);

            _ = Task.Run(async () =>
            {
                try
                {
                    await IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.PublishLiveEdits,
                        PayloadJson = JsonSerializer.Serialize(new UserLiveEditsState
                        {
                            ProjectId = projectId,
                            ClientId = GoogleDriveService.BuildClientId(username, Environment.MachineName),
                            User = username,
                            Machine = Environment.MachineName,
                            ElementUniqueIds = remainingOwned
                        })
                    }, timeoutMs: 10000);
                }
                catch { }
            });
        }

        public static void OnDocumentClosing(Document doc)
        {
            if (doc == null || !doc.IsWorkshared) return;
            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string username = doc.Application.Username;

            StatesByProject.TryRemove(projectId, out _);

            _ = Task.Run(async () =>
            {
                try
                {
                    await IpcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.PublishLiveEdits,
                        PayloadJson = JsonSerializer.Serialize(new UserLiveEditsState
                        {
                            ProjectId = projectId,
                            ClientId = GoogleDriveService.BuildClientId(username, Environment.MachineName),
                            User = username,
                            Machine = Environment.MachineName,
                            ElementUniqueIds = new List<string>()
                        })
                    }, timeoutMs: 8000);
                }
                catch { }
            });
        }

        /// <summary>
        /// Runs every 4s on a background thread (even when Revit is busy and not firing OnIdling!)
        /// pushing dirty local locks quickly (0.6s debounce / 2.5s cooldown) and executing a single
        /// unified PollConnectorState API call per open project.
        /// </summary>
        private static void BackgroundPollCallback(object? _)
        {
            if (_isSyncOrReloadActive) return;

            foreach (var state in StatesByProject.Values)
            {
                TriggerStateSyncAsync(state, forcePoll: false);
            }
        }

        public static void OnIdlingTick(UIApplication uiapp)
        {
            if (_isSyncOrReloadActive) return;

            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            var state = GetOrCreateState(projectId, doc.Application.Username);

            // Fast push from OnIdling if edits were just made
            if (state.HasPendingLocalPush)
            {
                TriggerStateSyncAsync(state, forcePoll: false);
            }
        }

        private static void TriggerStateSyncAsync(ProjectLiveState state, bool forcePoll)
        {
            if (string.IsNullOrWhiteSpace(state.ProjectId) || string.IsNullOrWhiteSpace(state.Username)) return;

            var now = DateTime.UtcNow;
            bool shouldPushLocal = false;
            List<string>? snapshotToPush = null;
            bool shouldPollRemote = false;

            lock (state.SyncRoot)
            {
                if (_isLiveLockEnabled)
                {
                    bool debouncedReady = state.HasPendingLocalPush &&
                                          (now - state.LastEditActivityUtc).TotalSeconds >= 0.6 &&
                                          (now - state.LastLocalPushUtc).TotalSeconds >= 2.5;

                    bool heartbeatDue = state.LocalDirty.Count > 0 &&
                                        (now - state.LastLocalPushUtc).TotalMinutes >= 4.0;

                    if (debouncedReady || heartbeatDue)
                    {
                        shouldPushLocal = true;
                        snapshotToPush = state.LocalDirty.ToList();
                        state.HasPendingLocalPush = false;
                        state.LastLocalPushUtc = now;
                    }
                }

                // Unified connector poll every 6s (1 API call covers BOTH live locks AND borrow requests!)
                if (forcePoll || (now - state.LastRemotePollUtc).TotalSeconds >= 6.0)
                {
                    shouldPollRemote = true;
                    state.LastRemotePollUtc = now;
                }
            }

            if (!shouldPushLocal && !shouldPollRemote) return;
            if (Interlocked.CompareExchange(ref state.IoBusyFlag, 1, 0) != 0) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    if (shouldPushLocal && snapshotToPush != null)
                    {
                        await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.PublishLiveEdits,
                            PayloadJson = JsonSerializer.Serialize(new UserLiveEditsState
                            {
                                ProjectId = state.ProjectId,
                                ClientId = GoogleDriveService.BuildClientId(state.Username, Environment.MachineName),
                                User = state.Username,
                                Machine = Environment.MachineName,
                                ElementUniqueIds = snapshotToPush
                            })
                        }, timeoutMs: 12000);
                    }

                    if (shouldPollRemote)
                    {
                        var resp = await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.PollConnectorState,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = state.ProjectId,
                                User = state.Username,
                                Machine = Environment.MachineName
                            })
                        }, timeoutMs: 12000);

                        if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                        {
                            var poll = JsonSerializer.Deserialize<ConnectorPollSnapshot>(resp.DataJson);
                            if (poll != null)
                            {
                                var newDict = new ConcurrentDictionary<string, RemoteElementLockInfo>(StringComparer.OrdinalIgnoreCase);
                                foreach (var l in poll.RemoteLiveLocks)
                                {
                                    newDict[l.ElementUniqueId] = l;
                                }
                                state.RemoteLocks = newDict;

                                // Forward borrow notifications from the SAME single API call!
                                BorrowNotificationService.ProcessPolledRequests(
                                    state.Username,
                                    poll.PendingBorrowRequests,
                                    poll.OutgoingBorrowRequests);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("LiveElementLockService", $"Background sync warning su '{state.ProjectId}': {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref state.IoBusyFlag, 0);
                }
            });
        }

        public static bool ShouldTrackElement(Element elem)
        {
            if (elem == null || elem is ElementType || elem is View) return false;
            if (elem.Category == null) return false;

            if (Enum.IsDefined(typeof(BuiltInCategory), elem.Category.Id.Value))
            {
                var bic = (BuiltInCategory)elem.Category.Id.Value;
                if (ExcludedCategories.Contains(bic)) return false;
            }

            return true;
        }

        public static bool TryGetCachedUniqueId(string projectId, ElementId id, out string? uniqueId)
        {
            uniqueId = null;
            if (StatesByProject.TryGetValue(projectId, out var state))
            {
                return state.ElementIdMap.TryGetValue(id.Value, out uniqueId);
            }
            return false;
        }

        public static bool TryGetRemoteLock(Document doc, string elementUniqueId, out RemoteElementLockInfo? lockInfo)
        {
            lockInfo = null;
            if (doc == null || !_isLiveLockEnabled || string.IsNullOrEmpty(elementUniqueId)) return false;
            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            return TryGetRemoteLock(projectId, elementUniqueId, out lockInfo);
        }

        public static bool TryGetRemoteLock(string projectId, string elementUniqueId, out RemoteElementLockInfo? lockInfo)
        {
            lockInfo = null;
            if (!_isLiveLockEnabled || string.IsNullOrEmpty(elementUniqueId)) return false;

            if (StatesByProject.TryGetValue(projectId, out var state))
            {
                return state.RemoteLocks.TryGetValue(elementUniqueId, out lockInfo);
            }
            return false;
        }

        public static void NotifyConflictToast(RemoteElementLockInfo lockInfo, bool isWarningOnly = false)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastToastShownUtc).TotalSeconds < 2.0) return;
            _lastToastShownUtc = now;

            try
            {
                App.UiDispatcher?.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var dlg = new System.Windows.Window
                        {
                            Title = Loc.Tr("LiveLock_Title"),
                            Width = 430,
                            Height = 148,
                            WindowStyle = System.Windows.WindowStyle.ToolWindow,
                            ResizeMode = System.Windows.ResizeMode.NoResize,
                            Topmost = true,
                            ShowInTaskbar = false,
                            Background = System.Windows.Media.Brushes.White,
                            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
                        };

                        var workArea = System.Windows.SystemParameters.WorkArea;
                        dlg.Left = workArea.Right - dlg.Width - 16;
                        dlg.Top = workArea.Bottom - dlg.Height - 16;

                        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(14) };
                        panel.Children.Add(new System.Windows.Controls.TextBlock
                        {
                            Text = isWarningOnly
                                ? $"⚠️ {Loc.Tr("LiveLock_Title")} (In modifica)"
                                : $"🔒 {Loc.Tr("LiveLock_Title")}",
                            FontWeight = System.Windows.FontWeights.Bold,
                            FontSize = 13.5,
                            Foreground = isWarningOnly ? System.Windows.Media.Brushes.DarkOrange : System.Windows.Media.Brushes.DarkRed,
                            Margin = new System.Windows.Thickness(0, 0, 0, 6)
                        });
                        panel.Children.Add(new System.Windows.Controls.TextBlock
                        {
                            Text = Loc.Format("LiveLock_BlockedDetail", lockInfo.OwnerUser, lockInfo.OwnerMachine),
                            TextWrapping = System.Windows.TextWrapping.Wrap,
                            FontSize = 12,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85))
                        });
                        dlg.Content = panel;
                        dlg.Show();

                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(isWarningOnly ? 4.0 : 5.5) };
                        timer.Tick += (s, e) =>
                        {
                            timer.Stop();
                            try { dlg.Close(); } catch { }
                        };
                        timer.Start();
                    }
                    catch { }
                }));
            }
            catch { }
        }
    }
}
