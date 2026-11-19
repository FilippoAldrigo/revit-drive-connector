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
 string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
 projectName = SynchronizationCoordinator.ActiveProjectId 
 ?? Path.GetFileNameWithoutExtension(centralPath);

 if (projectName.EndsWith("_Central", StringComparison.OrdinalIgnoreCase))
 {
 projectName = projectName.Substring(0, projectName.Length - "_Central".Length);
 }
 string userSuffix = "_" + doc.Application.Username;
 if (projectName.EndsWith(userSuffix, StringComparison.OrdinalIgnoreCase))
 {
 projectName = projectName.Substring(0, projectName.Length - userSuffix.Length);
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
