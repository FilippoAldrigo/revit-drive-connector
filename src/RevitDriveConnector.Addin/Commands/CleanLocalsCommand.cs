using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.UI;

namespace RevitDriveConnector.Addin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CleanLocalsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var win = new CleanLocalsWindow(commandData.Application);
            win.ShowDialog();
            return Result.Succeeded;
        }
    }
}
