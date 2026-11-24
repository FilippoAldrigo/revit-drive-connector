using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.Localization;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class LiveEditsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Live Locks", "Nessun documento attivo in Revit.");
                return Result.Cancelled;
            }
            var doc = uidoc.Document;

            if (doc == null || !doc.IsWorkshared)
            {
                TaskDialog.Show("Live Locks", "Il comando è disponibile solo per modelli condivisi (Workshared).");
                return Result.Cancelled;
            }

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath) &&
                !SynchronizationCoordinator.IsDriveManagedPath(doc.PathName))
            {
                TaskDialog.Show("Live Locks", "Il modello attivo non risulta gestito da Google Drive Connector.");
                return Result.Cancelled;
            }

            var win = new LiveEditsWindow(uidoc);
            win.ShowDialog();
            return Result.Succeeded;
        }
    }
}
