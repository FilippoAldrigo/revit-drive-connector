using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Addin.Services
{
    public class LinkItemInfo
    {
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public string Name { get; set; } = string.Empty;
        public string CurrentPath { get; set; } = string.Empty;
        public string ResolvedPath { get; set; } = string.Empty;
        public string TargetRelativePath { get; set; } = string.Empty;
        public ExternalFileReferenceType ReferenceType { get; set; }
        public PathType PathType { get; set; }
        public LinkedFileStatus Status { get; set; }
        public bool IsInsideDriveOrRelative { get; set; }
        public bool ExistsLocally { get; set; }
        public string StatusDescription { get; set; } = string.Empty;
        public string StatusColor { get; set; } = "#10B981";
    }

    public static class ExternalLinkManagerService
    {
        public static List<LinkItemInfo> GetProjectLinks(Document doc)
        {
            var result = new List<LinkItemInfo>();
            if (doc == null) return result;

            string projectDir = GetProjectBaseDirectory(doc);
            var refIds = ExternalFileUtils.GetAllExternalFileReferences(doc);

            foreach (var elemId in refIds)
            {
                try
                {
                    var extRef = ExternalFileUtils.GetExternalFileReference(doc, elemId);
                    if (extRef == null) continue;

                    var modelPath = extRef.GetPath();
                    string pathStr = modelPath != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(modelPath) : string.Empty;
                    string fileName = Path.GetFileName(pathStr);
                    if (string.IsNullOrEmpty(fileName)) fileName = $"Link_{elemId.Value}";

                    var status = extRef.GetLinkedFileStatus();
                    var pathType = extRef.PathType;
                    var refType = extRef.ExternalFileReferenceType;

                    bool isRel = pathType == PathType.Relative || pathStr.StartsWith("..\\", StringComparison.OrdinalIgnoreCase) || pathStr.StartsWith(".\\", StringComparison.OrdinalIgnoreCase);
                    string resolvedPath = pathStr;
                    try
                    {
                        var absMp = extRef.GetAbsolutePath();
                        if (absMp != null)
                        {
                            string absStr = ModelPathUtils.ConvertModelPathToUserVisiblePath(absMp);
                            if (!string.IsNullOrEmpty(absStr)) resolvedPath = absStr;
                        }
                        else if (isRel && !string.IsNullOrEmpty(doc.PathName))
                        {
                            string docDir = Path.GetDirectoryName(doc.PathName) ?? string.Empty;
                            if (!string.IsNullOrEmpty(docDir)) resolvedPath = Path.GetFullPath(Path.Combine(docDir, pathStr));
                        }
                    }
                    catch { }

                    bool exists = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath);
                    bool isInsideDrive = isRel || (!string.IsNullOrEmpty(projectDir) && resolvedPath.IndexOf(projectDir, StringComparison.OrdinalIgnoreCase) >= 0) || resolvedPath.IndexOf("RevitProjects", StringComparison.OrdinalIgnoreCase) >= 0;

                    string statusDesc;
                    string color;

                    if (status == LinkedFileStatus.NotFound || (!exists && status != LinkedFileStatus.Loaded))
                    {
                        statusDesc = "Non Trovato";
                        color = "#EF4444";
                    }
                    else if (!isInsideDrive && pathType == PathType.Absolute)
                    {
                        statusDesc = "Percorso Assoluto Fuori Drive";
                        color = "#F59E0B";
                    }
                    else if (isRel)
                    {
                        statusDesc = "Percorso Relativo Cloud";
                        color = "#10B981";
                    }
                    else
                    {
                        statusDesc = "Condiviso su Drive";
                        color = "#10B981";
                    }

                    result.Add(new LinkItemInfo
                    {
                        ElementId = elemId,
                        Name = fileName,
                        CurrentPath = pathStr,
                        ResolvedPath = resolvedPath,
                        TargetRelativePath = $@"..\Links\{fileName}",
                        ReferenceType = refType,
                        PathType = pathType,
                        Status = status,
                        IsInsideDriveOrRelative = isInsideDrive,
                        ExistsLocally = exists,
                        StatusDescription = statusDesc,
                        StatusColor = color
                    });
                }
                catch
                {
                }
            }

            return result;
        }

        public static (int consolidated, int failed, List<string> errors) ConsolidateAndRelativizeLinks(Document doc, Action<string>? log = null)
        {
            int consolidated = 0;
            int failed = 0;
            var errors = new List<string>();

            if (doc == null) return (0, 0, errors);

            string projectDir = GetProjectBaseDirectory(doc);
            if (string.IsNullOrEmpty(projectDir))
            {
                errors.Add("Impossibile determinare la cartella del progetto.");
                return (0, 0, errors);
            }

            string localLinksDir = Path.Combine(projectDir, "Links");
            Directory.CreateDirectory(localLinksDir);

            var links = GetProjectLinks(doc);
            var toFix = links.Where(l => !l.IsInsideDriveOrRelative || l.PathType == PathType.Absolute || l.Status == LinkedFileStatus.NotFound).ToList();

            if (toFix.Count == 0)
            {
                log?.Invoke("Tutti i collegamenti sono già conformi e impostati con percorsi relativi.");
                return (0, 0, errors);
            }

            LiveElementLockService.IsSyncOrReloadActive = true;
            try
            {
            using (var trans = new Transaction(doc, "Consolida Collegamenti Drive"))
            {
                trans.Start();

                foreach (var item in toFix)
                {
                    try
                    {
                        string fileName = item.Name;
                        string targetLocalFile = Path.Combine(localLinksDir, fileName);

                        string sourcePath = !string.IsNullOrEmpty(item.ResolvedPath) && File.Exists(item.ResolvedPath)
                            ? item.ResolvedPath
                            : item.CurrentPath;

                        if (File.Exists(sourcePath))
                        {
                            if (!File.Exists(targetLocalFile) || new FileInfo(sourcePath).Length != new FileInfo(targetLocalFile).Length)
                            {
                                File.Copy(sourcePath, targetLocalFile, true);
                            }
                        }

                        if (!File.Exists(targetLocalFile))
                        {
                            failed++;
                            errors.Add($"File '{fileName}' non reperibile sul disco locale per il consolidamento.");
                            continue;
                        }

                        var elem = doc.GetElement(item.ElementId);
                        if (elem is RevitLinkType revitLink)
                        {
                            var targetModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(targetLocalFile);
                            revitLink.LoadFrom(targetModelPath, new WorksetConfiguration());
                            revitLink.PathType = PathType.Relative;
                            consolidated++;
                            log?.Invoke($" Revit Link '{fileName}' consolidato in Links\\ con percorso relativo.");
                        }
                        else if (elem is CADLinkType cadLink)
                        {
                            cadLink.LoadFrom(targetLocalFile);
                            consolidated++;
                            log?.Invoke($" CAD Link '{fileName}' consolidato in Links\\.");
                        }
                        else
                        {
                            failed++;
                            errors.Add($"Tipo collegamento '{fileName}' ({elem?.GetType().Name ?? "Sconosciuto"}) non supporta il ricaricamento automatico.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"Errore consolidamento '{item.Name}': {ex.Message}");
                    }
                }

                trans.Commit();
            }
            }
            finally
            {
                LiveElementLockService.IsSyncOrReloadActive = false;
            }

            return (consolidated, failed, errors);
        }

        public static void AutoHealMissingLinks(Document doc)
        {
            try
            {
                if (doc == null || !doc.IsWorkshared) return;

                string projectDir = GetProjectBaseDirectory(doc);
                if (string.IsNullOrEmpty(projectDir)) return;

                string localLinksDir = Path.Combine(projectDir, "Links");

                var links = GetProjectLinks(doc);
                var broken = links.Where(l =>
                    l.Status == LinkedFileStatus.NotFound ||
                    (l.Status != LinkedFileStatus.Loaded && !l.ExistsLocally && !string.IsNullOrEmpty(l.Name))).ToList();

                if (broken.Count == 0) return;

                int healedCount = 0;
                using (var trans = new Transaction(doc, "Auto-Risoluzione Collegamenti Drive"))
                {
                    trans.Start();

                    foreach (var item in broken)
                    {
                        string fileName = item.Name;
                        string candidateLocal = Path.Combine(localLinksDir, fileName);

                        if (File.Exists(candidateLocal))
                        {
                            var elem = doc.GetElement(item.ElementId);
                            if (elem is RevitLinkType revitLink)
                            {
                                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(candidateLocal);
                                revitLink.LoadFrom(modelPath, new WorksetConfiguration());
                                revitLink.PathType = PathType.Relative;
                                healedCount++;
                            }
                            else if (elem is CADLinkType cadLink)
                            {
                                cadLink.LoadFrom(candidateLocal);
                                healedCount++;
                            }
                        }
                    }

                    if (healedCount > 0)
                    {
                        trans.Commit();
                        TaskDialog.Show("Drive Connector - Auto-Risoluzione Link",
                            $" Sono stati risolti e ricollegati automaticamente {healedCount} collegamenti esterni (Revit / CAD) trovati nella cartella cloud di Google Drive.");
                    }
                    else
                    {
                        trans.RollBack();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ExternalLinkManager", "Errore auto-heal link", ex);
            }
        }

        public static string GetProjectBaseDirectory(Document doc)
        {
            try
            {
                string path = doc.PathName;
                if (string.IsNullOrEmpty(path))
                {
                    path = SynchronizationCoordinator.GetCentralModelPath(doc);
                }

                if (string.IsNullOrEmpty(path)) return string.Empty;

                string dir = Path.GetDirectoryName(path) ?? "";
                if (dir.EndsWith("Central", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("Local", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetDirectoryName(dir) ?? dir;
                }

                return dir;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
