using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 [Regeneration(RegenerationOption.Manual)]
 public class GoogleAccountCommand : IExternalCommand
 {
 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 try
 {
 var win = new GoogleAccountWindow();
 win.ShowDialog();
 return Result.Succeeded;
 }
 catch (Exception ex)
 {
 message = ex.Message;
 TaskDialog.Show("Errore Account Google", $"Impossibile aprire Gestione Account Google: {ex.Message}");
 return Result.Failed;
 }
 }
 }
}
