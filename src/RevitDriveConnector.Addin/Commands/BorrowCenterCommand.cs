using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 public class BorrowCenterCommand : IExternalCommand
 {
 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 var uiapp = commandData.Application;
 var uidoc = uiapp.ActiveUIDocument;
 if (uidoc == null)
 {
 TaskDialog.Show("Centro Prestiti", "Nessun progetto aperto in Revit.");
 return Result.Cancelled;
 }

 var doc = uidoc.Document;
 if (!doc.IsWorkshared)
 {
 TaskDialog.Show("Centro Prestiti", "Il progetto corrente non è un modello condiviso (Workshared).");
 return Result.Cancelled;
 }

 var win = new BorrowCenterWindow(uiapp);
 win.ShowDialog();
 return Result.Succeeded;
 }
 }
}
