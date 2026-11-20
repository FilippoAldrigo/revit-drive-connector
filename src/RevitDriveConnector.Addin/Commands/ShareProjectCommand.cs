using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 public class ShareProjectCommand : IExternalCommand
 {
 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 var uiapp = commandData.Application;
 var uidoc = uiapp.ActiveUIDocument;

 string projectName = "";

 if (uidoc != null && uidoc.Document != null)
 {
 var doc = uidoc.Document;
 string centralPath = doc.IsWorkshared ? SynchronizationCoordinator.GetCentralModelPath(doc) : string.Empty;
 if (SynchronizationCoordinator.IsDriveManagedPath(doc.PathName) ||
 SynchronizationCoordinator.IsDriveManagedPath(centralPath))
 {
 projectName = !string.IsNullOrEmpty(centralPath)
 ? SynchronizationCoordinator.ExtractProjectName(centralPath)
 : (SynchronizationCoordinator.ActiveProjectId ?? Path.GetFileNameWithoutExtension(doc.PathName));
 }
 }

 if (string.IsNullOrWhiteSpace(projectName))
 {
 // Nessun progetto attivo: apri la finestra di selezione progetti per scegliere quale condividere
 var openWindow = new OpenProjectWindow(uiapp);
 openWindow.ShowDialog();
 return Result.Succeeded;
 }

 var shareWindow = new ShareProjectWindow(projectName);
 shareWindow.ShowDialog();
 return Result.Succeeded;
 }
 }
}
