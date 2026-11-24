using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ProjectHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var doc = uiapp.ActiveUIDocument?.Document;

            if (doc == null || !doc.IsWorkshared)
            {
                TaskDialog.Show("Cronologia Versioni", "Il comando è disponibile solo per modelli centrali o locali condivisi.");
                return Result.Cancelled;
            }

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath) &&
                !SynchronizationCoordinator.IsDriveManagedPath(doc.PathName))
            {
                TaskDialog.Show("Cronologia Versioni", "Il modello attivo non risulta gestito da Google Drive Connector.");
                return Result.Cancelled;
            }

            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string user = doc.Application.Username;

            var win = new ProjectHistoryWindow(projectId, user);
            win.ShowDialog();
            return Result.Succeeded;
        }
    }
}
