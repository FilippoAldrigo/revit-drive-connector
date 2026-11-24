using System;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CheckLinkUpdatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || !doc.IsWorkshared)
            {
                TaskDialog.Show("Aggiornamento Link", "Il comando è disponibile solo per modelli condivisi con collegamenti esterni.");
                return Result.Cancelled;
            }

            try
            {
                var task = ExternalLinkManagerService.CheckAndPromptCloudLinkUpdatesAsync(doc, silentIfUpToDate: false);
                task.Wait();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Errore Link", ex.Message);
                return Result.Failed;
            }
        }
    }
}
