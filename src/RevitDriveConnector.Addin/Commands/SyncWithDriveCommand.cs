using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.Localization;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 public class SyncWithDriveCommand : IExternalCommand
 {
 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 var uiapp = commandData.Application;
 var uidoc = uiapp.ActiveUIDocument;
 if (uidoc == null)
 {
 TaskDialog.Show(Loc.Tr("Sync_Title"), Loc.Tr("Cmd_NoActiveDoc"));
 return Result.Cancelled;
 }

 var doc = uidoc.Document;
 if (!doc.IsWorkshared)
 {
 TaskDialog.Show(Loc.Tr("Sync_Title"), Loc.Tr("Cmd_NotWorkshared"));
 return Result.Cancelled;
 }

 string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
 if (!SynchronizationCoordinator.IsDriveManagedPath(centralPath))
 {
 var td = new TaskDialog(Loc.Tr("Sync_Title"))
 {
 MainInstruction = "The model does not appear to be located in the Google Drive cache.",
 MainContent = $"Central path:\n{centralPath}\n\nProceed with standard Revit synchronization anyway?",
 CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
 };

 if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
 }

 try
 {
 // Opzioni di sincronizzazione nativa con il centrale
 var transSaveOptions = new TransactWithCentralOptions();
 var syncOptions = new SynchronizeWithCentralOptions
 {
 Comment = $"Drive Connector sync by {doc.Application.Username} on {DateTime.Now:g}",
 SaveLocalBefore = true,
 SaveLocalAfter = true,
 Compact = false
 };

 // Rilascia i workset e gli elementi presi in prestito affinché i collaboratori possano lavorare liberamente
 var relOptions = new RelinquishOptions(true)
 {
 StandardWorksets = true,
 ViewWorksets = true,
 FamilyWorksets = true,
 UserWorksets = true,
 CheckedOutElements = true
 };
 syncOptions.SetRelinquishOptions(relOptions);

 // Esegue la sincronizzazione: questo scatena automaticamente
 // DocumentSynchronizingWithCentral (lock + pull) e DocumentSynchronizedWithCentral (push + unlock)
 doc.SynchronizeWithCentral(transSaveOptions, syncOptions);

 if (SynchronizationCoordinator.LastSyncCancelled)
 {
 return Result.Cancelled;
 }

 TaskDialog.Show(Loc.Tr("Sync_Title"), Loc.Tr("Sync_Success"));
 return Result.Succeeded;
 }
 catch (Exception ex)
 {
 message = ex.Message;
 TaskDialog.Show(Loc.Tr("Sync_Title"), Loc.Format("Sync_Error", ex.Message));
 return Result.Failed;
 }
 }
 }
}
