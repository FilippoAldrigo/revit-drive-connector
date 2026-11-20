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
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Services
{
    /// <summary>
    /// Dynamic Model Updater (IUpdater) that prevents a user from modifying or deleting
    /// elements currently live-locked (edited but not yet synced) by another teammate.
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

            var modifiedIds = data.GetModifiedElementIds();
            var deletedIds = data.GetDeletedElementIds();

            if (modifiedIds.Count == 0 && deletedIds.Count == 0) return;

            foreach (var elemId in modifiedIds)
            {
                var elem = doc.GetElement(elemId);
                if (elem == null || !LiveElementLockService.ShouldTrackElement(elem)) continue;

                if (LiveElementLockService.TryGetRemoteLock(doc, elem.UniqueId, out var lockInfo) && lockInfo != null)
                {
                    var fm = new FailureMessage(_failureId);
                    fm.SetFailingElement(elemId);
                    doc.PostFailure(fm);
                    LiveElementLockService.NotifyConflictToast(lockInfo, elem.Category?.Name ?? elem.Name);
                    return;
                }
            }

            foreach (var delId in deletedIds)
            {
                if (LiveElementLockService.TryGetCachedUniqueId(doc, delId, out string? uid) &&
                    !string.IsNullOrEmpty(uid) &&
                    LiveElementLockService.TryGetRemoteLock(doc, uid, out var lockInfo) && lockInfo != null)
                {
                    var fm = new FailureMessage(_failureId);
                    fm.SetFailingElement(delId);
                    doc.PostFailure(fm);
                    LiveElementLockService.NotifyConflictToast(lockInfo, "Elemento");
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
        private static readonly NamedPipeIpcClient IpcClient = new();

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
            BuiltInCategory.OST_SunStudy
        };

        // Per-document state keyed by Document.GetHashCode() / central path
        private static readonly ConcurrentDictionary<string, HashSet<string>> LocalDirtyByProject = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RemoteElementLockInfo>> RemoteLocksByProject = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<long, string>> ElementIdMapByProject = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LocalSetLock = new();

        private static LiveElementLockUpdater? _updater;
        private static volatile bool _isSyncOrReloadActive = false;
        private static volatile bool _isLiveLockEnabled = true;

        private static DateTime _lastRemotePollUtc = DateTime.MinValue;
        private static DateTime _lastLocalPushUtc = DateTime.MinValue;
        private static DateTime _lastEditActivityUtc = DateTime.MinValue;
        private static DateTime _lastToastShownUtc = DateTime.MinValue;
        private static bool _hasPendingLocalPush = false;
        private static bool _isIoBusy = false;

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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LiveElementLockService] Initialize updater warning: {ex.Message}");
            }
        }

        public static void Shutdown()
        {
            try
            {
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

        public static void OnDocumentOpened(Document doc)
        {
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            SeedElementLookupCache(doc, projectId);
            _lastRemotePollUtc = DateTime.MinValue; // Force prompt fetch on Idling
        }

        public static void SeedElementLookupCache(Document doc, string projectId)
        {
            try
            {
                var map = ElementIdMapByProject.GetOrAdd(projectId, _ => new ConcurrentDictionary<long, string>());
                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();
                foreach (var elem in collector)
                {
                    if (elem != null && elem.Id != null && !string.IsNullOrEmpty(elem.UniqueId))
                    {
                        map[elem.Id.Value] = elem.UniqueId;
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
                t.Contains("Relinquish", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            var idMap = ElementIdMapByProject.GetOrAdd(projectId, _ => new ConcurrentDictionary<long, string>());
            var dirtySet = LocalDirtyByProject.GetOrAdd(projectId, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            bool changed = false;

            lock (LocalSetLock)
            {
                foreach (var id in e.GetAddedElementIds())
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;
                    idMap[id.Value] = elem.UniqueId;

                    if (!ShouldTrackElement(elem)) continue;

                    if (e.Operation == UndoOperation.TransactionUndone)
                    {
                        if (WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (dirtySet.Remove(elem.UniqueId)) changed = true;
                        }
                    }
                    else
                    {
                        if (dirtySet.Add(elem.UniqueId)) changed = true;
                    }
                }

                foreach (var id in e.GetModifiedElementIds())
                {
                    var elem = doc.GetElement(id);
                    if (elem == null) continue;
                    idMap[id.Value] = elem.UniqueId;

                    if (!ShouldTrackElement(elem)) continue;

                    var checkoutStatus = WorksharingUtils.GetCheckoutStatus(doc, id);
                    if (e.Operation == UndoOperation.TransactionUndone)
                    {
                        if (checkoutStatus != CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (dirtySet.Remove(elem.UniqueId)) changed = true;
                        }
                    }
                    else
                    {
                        if (checkoutStatus == CheckoutStatus.OwnedByCurrentUser)
                        {
                            if (dirtySet.Add(elem.UniqueId)) changed = true;
                        }
                    }
                }

                foreach (var id in e.GetDeletedElementIds())
                {
                    if (idMap.TryGetValue(id.Value, out string? uid) && !string.IsNullOrEmpty(uid))
                    {
                        if (e.Operation == UndoOperation.TransactionUndone)
                        {
                            if (dirtySet.Remove(uid)) changed = true;
                        }
                        else
                        {
                            if (dirtySet.Add(uid)) changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                _hasPendingLocalPush = true;
                _lastEditActivityUtc = DateTime.UtcNow;
            }
        }

        public static void OnDocumentSynchronized(Document doc)
        {
            if (doc == null || !doc.IsWorkshared) return;
            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string username = doc.Application.Username;

            List<string> remainingOwned = new();
            lock (LocalSetLock)
            {
                if (LocalDirtyByProject.TryGetValue(projectId, out var dirtySet))
                {
                    var toRemove = new List<string>();
                    foreach (var uid in dirtySet)
                    {
                        var elem = doc.GetElement(uid);
                        if (elem == null || WorksharingUtils.GetCheckoutStatus(doc, elem.Id) != CheckoutStatus.OwnedByCurrentUser)
                        {
                            toRemove.Add(uid);
                        }
                    }

                    foreach (var uid in toRemove)
                    {
                        dirtySet.Remove(uid);
                    }

                    remainingOwned = dirtySet.ToList();
                }
            }

            SeedElementLookupCache(doc, projectId);
            _hasPendingLocalPush = false;
            _lastLocalPushUtc = DateTime.UtcNow;
            _lastRemotePollUtc = DateTime.MinValue; // Refresh remote locks soon

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

            lock (LocalSetLock)
            {
                LocalDirtyByProject.TryRemove(projectId, out _);
            }
            RemoteLocksByProject.TryRemove(projectId, out _);
            ElementIdMapByProject.TryRemove(projectId, out _);
            _hasPendingLocalPush = false;

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
                            User = username,
                            Machine = Environment.MachineName,
                            ElementUniqueIds = new List<string>()
                        })
                    }, timeoutMs: 8000);
                }
                catch { }
            });
        }

        public static void OnIdlingTick(UIApplication uiapp)
        {
            if (!_isLiveLockEnabled || _isSyncOrReloadActive || _isIoBusy) return;

            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared || doc.IsFamilyDocument) return;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string username = doc.Application.Username;
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(username)) return;

            var now = DateTime.UtcNow;

            bool shouldPushLocal = false;
            List<string>? snapshotToPush = null;

            lock (LocalSetLock)
            {
                if (LocalDirtyByProject.TryGetValue(projectId, out var dirtySet))
                {
                    bool debouncedReady = _hasPendingLocalPush &&
                                          (now - _lastEditActivityUtc).TotalSeconds >= 1.5 &&
                                          (now - _lastLocalPushUtc).TotalSeconds >= 6.0;

                    bool heartbeatDue = dirtySet.Count > 0 &&
                                        (now - _lastLocalPushUtc).TotalMinutes >= 5.0;

                    if (debouncedReady || heartbeatDue)
                    {
                        shouldPushLocal = true;
                        snapshotToPush = dirtySet.ToList();
                        _hasPendingLocalPush = false;
                        _lastLocalPushUtc = now;
                    }
                }
            }

            bool shouldPollRemote = (now - _lastRemotePollUtc).TotalSeconds >= 10.0;
            if (!shouldPushLocal && !shouldPollRemote) return;

            if (shouldPollRemote)
            {
                _lastRemotePollUtc = now;
            }

            _isIoBusy = true;
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
                                ProjectId = projectId,
                                User = username,
                                Machine = Environment.MachineName,
                                ElementUniqueIds = snapshotToPush
                            })
                        }, timeoutMs: 10000);
                    }

                    if (shouldPollRemote)
                    {
                        var resp = await IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.GetRemoteLiveEdits,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = projectId,
                                User = username
                            })
                        }, timeoutMs: 10000);

                        if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                        {
                            var locks = JsonSerializer.Deserialize<List<RemoteElementLockInfo>>(resp.DataJson);
                            var newDict = new ConcurrentDictionary<string, RemoteElementLockInfo>(StringComparer.OrdinalIgnoreCase);
                            if (locks != null)
                            {
                                foreach (var l in locks)
                                {
                                    newDict[l.ElementUniqueId] = l;
                                }
                            }
                            RemoteLocksByProject[projectId] = newDict;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LiveElementLockService] Background sync warning: {ex.Message}");
                }
                finally
                {
                    _isIoBusy = false;
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

        public static bool TryGetCachedUniqueId(Document doc, ElementId id, out string? uniqueId)
        {
            uniqueId = null;
            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            if (ElementIdMapByProject.TryGetValue(projectId, out var map))
            {
                return map.TryGetValue(id.Value, out uniqueId);
            }
            return false;
        }

        public static bool TryGetRemoteLock(Document doc, string elementUniqueId, out RemoteElementLockInfo? lockInfo)
        {
            lockInfo = null;
            if (!_isLiveLockEnabled || string.IsNullOrEmpty(elementUniqueId)) return false;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);

            if (RemoteLocksByProject.TryGetValue(projectId, out var dict))
            {
                return dict.TryGetValue(elementUniqueId, out lockInfo);
            }
            return false;
        }

        public static void NotifyConflictToast(RemoteElementLockInfo lockInfo, string elemLabel)
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
                            Width = 420,
                            Height = 145,
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
                            Text = $"🔒 {Loc.Tr("LiveLock_Title")}",
                            FontWeight = System.Windows.FontWeights.Bold,
                            FontSize = 13.5,
                            Foreground = System.Windows.Media.Brushes.DarkRed,
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

                        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5.5) };
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
