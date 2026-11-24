using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Addin.Services
{
    public class LockedElementDisplayItem
    {
        public ElementId Id { get; set; } = ElementId.InvalidElementId;
        public string UniqueId { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string ElementName { get; set; } = string.Empty;
        public string OwnerUser { get; set; } = string.Empty;
        public string OwnerMachine { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
        public bool IsRemote { get; set; }
        public string ColorHex => IsRemote ? "#EF4444" : "#0284C7";
        public string StatusBadge => IsRemote ? $"Bloccato da {OwnerUser}" : "In modifica locale (Tu)";
    }

    public static class VisualLocksService
    {
        private static readonly HashSet<long> HighlightedElementIds = new();

        public static List<LockedElementDisplayItem> GetActiveLockedElements(Document doc)
        {
            var result = new List<LockedElementDisplayItem>();
            if (doc == null || !doc.IsWorkshared) return result;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath)) return result;

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            var (remoteLocks, localDirty) = LiveElementLockService.GetProjectLockSnapshots(projectId);

            string currentUser = doc.Application.Username;
            string currentMachine = Environment.MachineName;

            // 1. Remote locks (teammates)
            foreach (var kvp in remoteLocks)
            {
                try
                {
                    string uid = kvp.Key;
                    var lockInfo = kvp.Value;
                    var elem = doc.GetElement(uid);
                    if (elem != null && elem.Id != null)
                    {
                        result.Add(new LockedElementDisplayItem
                        {
                            Id = elem.Id,
                            UniqueId = uid,
                            CategoryName = elem.Category?.Name ?? "Elemento",
                            ElementName = string.IsNullOrWhiteSpace(elem.Name) ? $"{elem.Category?.Name} [{elem.Id.Value}]" : elem.Name,
                            OwnerUser = lockInfo.OwnerUser,
                            OwnerMachine = lockInfo.OwnerMachine,
                            UpdatedUtc = lockInfo.UpdatedUtc,
                            IsRemote = true
                        });
                    }
                }
                catch { }
            }

            // 2. Local dirty elements (current user)
            foreach (var uid in localDirty)
            {
                try
                {
                    var elem = doc.GetElement(uid);
                    if (elem != null && elem.Id != null && !result.Any(r => r.UniqueId.Equals(uid, StringComparison.OrdinalIgnoreCase)))
                    {
                        result.Add(new LockedElementDisplayItem
                        {
                            Id = elem.Id,
                            UniqueId = uid,
                            CategoryName = elem.Category?.Name ?? "Elemento",
                            ElementName = string.IsNullOrWhiteSpace(elem.Name) ? $"{elem.Category?.Name} [{elem.Id.Value}]" : elem.Name,
                            OwnerUser = currentUser,
                            OwnerMachine = currentMachine,
                            UpdatedUtc = DateTime.UtcNow,
                            IsRemote = false
                        });
                    }
                }
                catch { }
            }

            return result;
        }

        public static bool HighlightLocks(Document doc, View view, bool enable)
        {
            if (doc == null || view == null || view.IsTemplate) return false;

            LiveElementLockService.IsSyncOrReloadActive = true;
            try
            {
                using var trans = new Transaction(doc, enable ? "Evidenzia Lock Live" : "Ripristina Grafica Lock");
                trans.Start();

                if (!enable)
                {
                    foreach (var idVal in HighlightedElementIds.ToList())
                    {
                        try
                        {
                            var elemId = new ElementId(idVal);
                            view.SetElementOverrides(elemId, new OverrideGraphicSettings());
                        }
                        catch { }
                    }
                    HighlightedElementIds.Clear();
                    trans.Commit();
                    return true;
                }

                var items = GetActiveLockedElements(doc);
                if (items.Count == 0)
                {
                    trans.RollBack();
                    return false;
                }

                // Find solid fill pattern for surface override
                ElementId? solidFillPatternId = null;
                try
                {
                    var patterns = new FilteredElementCollector(doc)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .ToList();
                    var solid = patterns.FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
                    if (solid != null) solidFillPatternId = solid.Id;
                }
                catch { }

                var redColor = new Color(239, 68, 68);     // Teammates red
                var cyanColor = new Color(2, 132, 199);    // Current user cyan

                foreach (var item in items)
                {
                    try
                    {
                        var settings = new OverrideGraphicSettings();
                        var targetColor = item.IsRemote ? redColor : cyanColor;

                        settings.SetProjectionLineColor(targetColor);
                        settings.SetProjectionLineWeight(6);
                        settings.SetSurfaceTransparency(40);

                        if (solidFillPatternId != null)
                        {
                            settings.SetSurfaceForegroundPatternId(solidFillPatternId);
                            settings.SetSurfaceForegroundPatternColor(targetColor);
                            settings.SetSurfaceForegroundPatternVisible(true);
                        }

                        view.SetElementOverrides(item.Id, settings);
                        HighlightedElementIds.Add(item.Id.Value);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("VisualLocksService", $"Errore override elemento {item.Id.Value}", ex);
                    }
                }

                trans.Commit();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("VisualLocksService", "Errore durante highlight locks", ex);
                return false;
            }
            finally
            {
                LiveElementLockService.IsSyncOrReloadActive = false;
            }
        }

        public static bool IsolateLocks(UIDocument uidoc, bool remoteOnly = false)
        {
            if (uidoc == null) return false;
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            if (doc == null || view == null || view.IsTemplate) return false;

            var items = GetActiveLockedElements(doc);
            if (remoteOnly) items = items.Where(i => i.IsRemote).ToList();

            if (items.Count == 0) return false;

            var elemIds = items.Select(i => i.Id).ToList();
            try
            {
                view.IsolateElementsTemporary(elemIds);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("VisualLocksService", "Impossibile isolare elementi in vista temporanea", ex);
                return false;
            }
        }

        public static void ResetView(UIDocument uidoc)
        {
            if (uidoc == null) return;
            var doc = uidoc.Document;
            var view = uidoc.ActiveView;
            if (doc == null || view == null) return;

            // 1. Reset Temporary Hide/Isolate
            try
            {
                if (view.IsInTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate))
                {
                    view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                }
            }
            catch { }

            // 2. Clear graphic overrides if any were applied
            if (HighlightedElementIds.Count > 0)
            {
                HighlightLocks(doc, view, enable: false);
            }
        }
    }
}
