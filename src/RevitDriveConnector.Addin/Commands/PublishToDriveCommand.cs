using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PublishToDriveCommand : IExternalCommand
    {
        private static readonly NamedPipeIpcClient IpcClient = new();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show(Loc.Tr("Publish_Title"), Loc.Tr("Cmd_NoActiveDoc"));
                return Result.Cancelled;
            }

            var doc = uidoc.Document;

            string existingCentral = doc.IsWorkshared ? SynchronizationCoordinator.GetCentralModelPath(doc) : string.Empty;
            if (SynchronizationCoordinator.IsDriveManagedPath(doc.PathName) ||
                SynchronizationCoordinator.IsDriveManagedPath(existingCentral))
            {
                TaskDialog.Show(Loc.Tr("Publish_Title"), Loc.Tr("Publish_AlreadyPublished"));
                return Result.Cancelled;
            }

            try
            {
                var config = AppConfig.Load();
                string cacheRoot = !string.IsNullOrWhiteSpace(config.LocalCachePath) ? config.LocalCachePath : @"C:\RevitDriveCache";

                string projectName = Path.GetFileNameWithoutExtension(doc.PathName);
                if (string.IsNullOrWhiteSpace(projectName))
                {
                    projectName = !string.IsNullOrWhiteSpace(doc.Title)
                        ? Path.GetFileNameWithoutExtension(doc.Title)
                        : "RevitProject_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
                }

                if (projectName.EndsWith("_Central", StringComparison.OrdinalIgnoreCase))
                {
                    projectName = projectName.Substring(0, projectName.Length - "_Central".Length);
                }
                string userSuffix = "_" + doc.Application.Username;
                if (projectName.EndsWith(userSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    projectName = projectName.Substring(0, projectName.Length - userSuffix.Length);
                }

                // Se l'utente si trova nel file temporaneo Transition di un tentativo precedente,
                // recupera automaticamente il vero nome del progetto presente in RevitDriveCache
                if (projectName.Equals("Transition", StringComparison.OrdinalIgnoreCase) && Directory.Exists(cacheRoot))
                {
                    var realDirs = Directory.GetDirectories(cacheRoot)
                        .Select(Path.GetFileName)
                        .Where(n => !string.IsNullOrEmpty(n) && !n.Equals("Transition", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (realDirs.Count > 0)
                    {
                        projectName = realDirs[0]!;
                    }
                }

                var td = new TaskDialog(Loc.Tr("Publish_Title"))
                {
                    MainInstruction = Loc.Format("Publish_AskPublish", projectName),
                    MainContent = Loc.Tr("Publish_AskPublishContent"),
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };

                if (td.Show() != TaskDialogResult.Yes)
                {
                    return Result.Cancelled;
                }

                // 1. Se il documento non ha il worksharing attivo, lo attiviamo
                if (!doc.IsWorkshared)
                {
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                }

                // 2. Prepariamo la cartella locale di cache
                string projectDir = Path.Combine(cacheRoot, projectName);
                string centralDir = Path.Combine(projectDir, "Central");
                string localDir = Path.Combine(projectDir, "Local");
                string linksDir = Path.Combine(projectDir, "Links");

                Directory.CreateDirectory(centralDir);
                Directory.CreateDirectory(localDir);
                Directory.CreateDirectory(linksDir);

                try
                {
                    ExternalLinkManagerService.ConsolidateAndRelativizeLinks(doc);
                }
                catch { }

                string centralFilePath = Path.Combine(centralDir, $"{projectName}_Central.rvt");

                // 3. Salviamo come Modello Centrale e rilasciamo tutti i workset
                var saveOptions = new SaveAsOptions
                {
                    OverwriteExistingFile = true
                };
                var wsOptions = new WorksharingSaveAsOptions
                {
                    SaveAsCentral = true
                };
                saveOptions.SetWorksharingOptions(wsOptions);

                doc.SaveAs(centralFilePath, saveOptions);

                try
                {
                    var relinquishOpts = new RelinquishOptions(true)
                    {
                        StandardWorksets = true,
                        ViewWorksets = true,
                        FamilyWorksets = true,
                        UserWorksets = true,
                        CheckedOutElements = true
                    };
                    WorksharingUtils.RelinquishOwnership(doc, relinquishOpts, new TransactWithCentralOptions());
                }
                catch { }

                // 4. Spostiamo temporaneamente la sessione per liberare centralFilePath mentre eseguiamo l'upload iniziale su Google Drive
                string tempTransitionDir = Path.Combine(Path.GetTempPath(), "RevitDrivePublish_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempTransitionDir);
                string tempTransitionPath = Path.Combine(tempTransitionDir, "Transition.rvt");

                try
                {
                    doc.SaveAs(tempTransitionPath, saveOptions);
                }
                catch { }

                string username = doc.Application.Username;

                // 5. Esegue il caricamento su Google Drive PRIMA di aprire il Modello Locale
                //    (avviando automaticamente il login OAuth se non ancora connesso)
                var createReq = new
                {
                    ProjectName = projectName,
                    CentralPath = centralFilePath,
                    User = username
                };

                var respTask = System.Threading.Tasks.Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.CreateCloudProject,
                    PayloadJson = JsonSerializer.Serialize(createReq)
                }, timeoutMs: 300000));

                respTask.Wait();
                var resp = respTask.Result;

                if (!resp.Success)
                {
                    TaskDialog.Show(Loc.Tr("Publish_Title"), Loc.Format("Publish_ErrUpload", resp.ErrorMessage ?? ""));
                    return Result.Failed;
                }

                // 6. Crea e attiva il Modello Locale dell'utente dopo il completamento dell'upload iniziale
                string localFilePath = Path.Combine(localDir, $"{projectName}_{username}.rvt");
                string localBackupDir = Path.Combine(localDir, $"{projectName}_{username}_backup");

                SafeDeleteFileAndDirectory(localFilePath, localBackupDir);

                int suffix = 1;
                while (File.Exists(localFilePath) || Directory.Exists(localBackupDir))
                {
                    localFilePath = Path.Combine(localDir, $"{projectName}_{username}_{suffix}.rvt");
                    localBackupDir = Path.Combine(localDir, $"{projectName}_{username}_{suffix}_backup");
                    suffix++;
                }

                var centralModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(centralFilePath);
                var localModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(localFilePath);

                WorksharingUtils.CreateNewLocal(centralModelPath, localModelPath);
                SynchronizationCoordinator.ActiveProjectId = projectName;
                SynchronizationCoordinator.SuppressNextAutoReload = true;

                try
                {
                    var openOpts = new OpenOptions
                    {
                        DetachFromCentralOption = DetachFromCentralOption.DoNotDetach
                    };
                    RecentFilesService.RegisterInRevitRecentFiles(uiapp.Application.VersionNumber, localFilePath);
                    uiapp.OpenAndActivateDocument(localModelPath, openOpts, false);

                    try
                    {
                        if (doc.IsValidObject)
                        {
                            doc.Close(false);
                        }
                    }
                    catch { }
                }
                catch (Exception openEx)
                {
                    Console.WriteLine($"[PublishToDrive] Open local warning: {openEx.Message}");
                }
                finally
                {
                    SafeDeleteFileAndDirectory(tempTransitionPath, tempTransitionDir);
                    string oldTransitionCache = Path.Combine(cacheRoot, "Transition");
                    if (Directory.Exists(oldTransitionCache))
                    {
                        SafeDeleteFileAndDirectory("", oldTransitionCache);
                    }
                }

                var tdSuccess = new TaskDialog(Loc.Tr("Publish_Title"))
                {
                    MainInstruction = Loc.Format("Publish_Completed", projectName),
                    MainContent = Loc.Format("Publish_CompletedContent", localFilePath),
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                };

                if (tdSuccess.Show() == TaskDialogResult.Yes)
                {
                    try
                    {
                        var shareWin = new ShareProjectWindow(projectName);
                        shareWin.ShowDialog();
                    }
                    catch { }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(Loc.Tr("Publish_Title"), $"{Loc.Tr("Common_Error")}: {ex.Message}");
                return Result.Failed;
            }
        }

        private static void SafeDeleteFileAndDirectory(string filePath, string dirPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
                {
                    var di = new DirectoryInfo(dirPath);
                    foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            fi.Attributes = FileAttributes.Normal;
                        }
                        catch { }
                    }
                    di.Delete(true);
                }
            }
            catch { }
        }
    }
}
