using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 public class ManageLinksCommand : IExternalCommand
 {
 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 var uiapp = commandData.Application;
 var uidoc = uiapp.ActiveUIDocument;
 if (uidoc == null)
 {
 TaskDialog.Show("Collegamenti Drive", "Nessun progetto aperto in Revit.");
 return Result.Cancelled;
 }

 var win = new ManageLinksWindow(uiapp);
 win.ShowDialog();
 return Result.Succeeded;
 }
 }
}
