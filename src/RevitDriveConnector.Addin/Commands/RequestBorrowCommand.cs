using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class RequestBorrowCommand : IExternalCommand
    {
        private static readonly NamedPipeIpcClient IpcClient = new();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var uidoc = uiapp.ActiveUIDocument;
            string commandTitle = Loc.Tr("Btn_RequestBorrow").Replace("\n", " ");

            if (uidoc == null)
            {
                TaskDialog.Show(commandTitle, Loc.Tr("Cmd_NoActiveDoc"));
                return Result.Cancelled;
            }

            var doc = uidoc.Document;
            if (!doc.IsWorkshared)
            {
                TaskDialog.Show(commandTitle, Loc.Tr("Cmd_NotWorkshared"));
                return Result.Cancelled;
            }

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show(commandTitle, Loc.Tr("Borrow_SelectElement"));
                return Result.Cancelled;
            }

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
            string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
            string currentUsername = doc.Application.Username;

            // Allinea prima il modello centrale nella cache locale per leggere lo stato reale dei permessi dal Cloud
            SynchronizationCoordinator.AlignCentralFromCloudSync(doc);

            int sentCount = 0;
            int alreadyOwnedCount = 0;
            int freeAcquiredCount = 0;

            foreach (var elemId in selectedIds)
            {
                var elem = doc.GetElement(elemId);
                if (elem == null) continue;

                var status = WorksharingUtils.GetCheckoutStatus(doc, elemId, out string owner);

                if (status == CheckoutStatus.OwnedByCurrentUser)
                {
                    alreadyOwnedCount++;
                    continue;
                }

                if (status == CheckoutStatus.NotOwned)
                {
                    try
                    {
                        using var trans = new Transaction(doc, "Checkout element");
                        trans.Start();
                        WorksharingUtils.CheckoutElements(doc, new[] { elemId });
                        trans.Commit();
                        freeAcquiredCount++;
                    }
                    catch
                    {
                    }
                    continue;
                }

                if (status == CheckoutStatus.OwnedByOtherUser)
                {
                    string elemName = $"{elem.Category?.Name ?? "Element"}: {elem.Name} (ID: {elem.Id.Value})";

                    var td = new TaskDialog(Loc.Tr("Toast_Title"))
                    {
                        MainInstruction = Loc.Format("Borrow_OwnedBy", owner),
                        MainContent = Loc.Format("Borrow_AskRequest", owner, elemName),
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                    };

                    if (td.Show() == TaskDialogResult.Yes)
                    {
                        var req = new BorrowRequest
                        {
                            ProjectId = projectId,
                            ElementId = elem.Id.Value,
                            ElementName = elemName,
                            RequesterUser = currentUsername,
                            RequesterMachine = Environment.MachineName,
                            OwnerUser = owner,
                            Message = "Request from Revit"
                        };

                        try
                        {
                            var respTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                            {
                                Command = IpcCommand.SendBorrowRequest,
                                PayloadJson = JsonSerializer.Serialize(req)
                            }, timeoutMs: 8000));

                            respTask.Wait();
                            if (respTask.Result.Success)
                            {
                                sentCount++;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (freeAcquiredCount > 0 && !string.IsNullOrEmpty(centralPath))
            {
                // Propaga sul Cloud i nuovi permessi degli elementi presi in carico
                _ = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.UploadProjectChanges,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        ProjectId = projectId,
                        CentralPath = centralPath,
                        LockToken = "",
                        User = currentUsername
                    })
                }, timeoutMs: 60000));
            }

            if (sentCount > 0)
            {
                TaskDialog.Show(commandTitle, Loc.Format("Borrow_SentSuccess", sentCount));
            }
            else if (alreadyOwnedCount > 0 || freeAcquiredCount > 0)
            {
                TaskDialog.Show(commandTitle, Loc.Format("Borrow_AlreadyYours", alreadyOwnedCount + freeAcquiredCount));
            }

            return Result.Succeeded;
        }
    }
}
