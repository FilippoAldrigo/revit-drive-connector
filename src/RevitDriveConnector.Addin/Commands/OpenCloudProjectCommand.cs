using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.Commands
{
 [Transaction(TransactionMode.Manual)]
 public class OpenCloudProjectCommand : IExternalCommand
 {
 private static readonly NamedPipeIpcClient IpcClient = new();

 public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
 {
 var uiapp = commandData.Application;

 try
 {
 // Mostra la finestra WPF di selezione progetto
 var window = new OpenProjectWindow(uiapp);
 bool? result = window.ShowDialog();

 if (result != true || window.SelectedProject == null)
 {
 return Result.Cancelled;
 }

 var project = window.SelectedProject;
 SynchronizationCoordinator.ActiveProjectId = project.Id;

 return Result.Succeeded;
 }
 catch (Exception ex)
 {
 message = ex.Message;
 TaskDialog.Show("Errore Apertura Drive", $"Impossibile aprire il progetto cloud: {ex.Message}");
 return Result.Failed;
 }
 }
 }
}
