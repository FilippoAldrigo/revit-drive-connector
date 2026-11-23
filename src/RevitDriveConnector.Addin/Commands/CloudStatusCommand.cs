using System;
using System.Collections.Generic;
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
    public class CloudStatusCommand : IExternalCommand
    {
        private static readonly NamedPipeIpcClient IpcClient = new();

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiapp = commandData.Application;
            var doc = uiapp.ActiveUIDocument?.Document;

            try
            {
                string? projectName = null;
                if (doc != null && doc.IsWorkshared)
                {
                    string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
                    if (SynchronizationCoordinator.IsDriveManagedPath(doc.PathName) ||
                        SynchronizationCoordinator.IsDriveManagedPath(centralPath))
                    {
                        projectName = SynchronizationCoordinator.ExtractProjectName(centralPath);
                    }
                }

                var reqPayload = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["ProjectId"] = projectName ?? ""
                });

                var task = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.GetDaemonStatus,
                    PayloadJson = reqPayload
                }, timeoutMs: 8000));

                task.Wait();
                var resp = task.Result;

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    TaskDialog.Show(Loc.Tr("Status_Title"),
                        $"{Loc.Tr("Common_Error")}: {resp.ErrorMessage}\n\nRevitDriveConnector.Daemon is not responding.");
                    return Result.Failed;
                }

                var statusInfo = JsonSerializer.Deserialize<DaemonStatusInfo>(resp.DataJson) ?? new DaemonStatusInfo();

                string driveAccountStr = statusInfo.GoogleDriveConnected
                    ? $"CONNECTED ({statusInfo.GoogleAccountEmail})"
                    : $"Shared Drive Folder ({statusInfo.CloudStoragePath})";

                string lockStatusStr = !string.IsNullOrEmpty(statusInfo.CurrentLockHolder)
                    ? $"LOCKED by {statusInfo.CurrentLockHolder} ({statusInfo.CurrentLockMachine}) until {statusInfo.LockExpiresAtUtc?.ToLocalTime():HH:mm:ss}"
                    : "UNLOCKED (Available for Sync)";

                string activeUsersStr = statusInfo.ActiveUsers != null && statusInfo.ActiveUsers.Count > 0
                    ? string.Join(", ", statusInfo.ActiveUsers.Select(u => $"{u.UserName} ({u.MachineName})"))
                    : uiapp.Application.Username;

                string verStr = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.3.2";
                string liveLockStr = LiveElementLockService.IsLiveLockEnabled ? "ENABLED" : "DISABLED";

                var td = new TaskDialog(Loc.Tr("Status_Title"))
                {
                    MainInstruction = $"{Loc.Tr("Status_Main")} (v{verStr})",
                    MainContent =
                        $"• Cloud Services: {statusInfo.Status.ToUpperInvariant()}\n" +
                        $"• Google Drive: {driveAccountStr}\n" +
                        $"• Live Element Locks (LAN-style): {liveLockStr}\n" +
                        $"• NTP/UTC Time Sync: {(statusInfo.IsTimeSynchronized ? "SYNCHRONIZED" : "LOCAL UTC")} (Offset: {statusInfo.ClockOffsetMs:F0} ms)\n" +
                        $"• Clock Skew Tolerance: ±15s\n" +
                        $"• Revit User: {uiapp.Application.Username} ({Environment.MachineName})\n" +
                        (string.IsNullOrEmpty(projectName)
                            ? "• No cloud project currently open."
                            : $"• Active Project: {projectName}\n• Sync Lock State: {lockStatusStr}\n• Active Team Members: {activeUsersStr}"),
                    CommonButtons = TaskDialogCommonButtons.Close
                };

                if (!string.IsNullOrEmpty(projectName))
                {
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                        Loc.Tr("Status_ForceUnlock"),
                        Loc.Tr("Status_ForceUnlockDesc"));
                }

                var tdResult = td.Show();

                if (tdResult == TaskDialogResult.CommandLink1 && !string.IsNullOrEmpty(projectName))
                {
                    var confirm = new TaskDialog("Confirm Force Unlock")
                    {
                        MainInstruction = $"Force unlock for '{projectName}'?",
                        MainContent = "Ensure that no team member is actively performing a synchronization before forcing an unlock.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No
                    };

                    if (confirm.Show() == TaskDialogResult.Yes)
                    {
                        var breakTask = Task.Run(() => IpcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.ForceBreakLock,
                            PayloadJson = JsonSerializer.Serialize(new { ProjectId = projectName })
                        }, timeoutMs: 8000));

                        breakTask.Wait();
                        var breakResp = breakTask.Result;

                        if (breakResp.Success)
                        {
                            TaskDialog.Show(Loc.Tr("Status_Title"), $"Synchronization lock for '{projectName}' was successfully removed!");
                        }
                        else
                        {
                            TaskDialog.Show(Loc.Tr("Common_Error"), $"Could not force unlock: {breakResp.ErrorMessage}");
                        }
                    }
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(Loc.Tr("Common_Error"), $"{Loc.Tr("Common_Error")}: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}
