using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDriveConnector.Addin.Commands
{
    /// <summary>
    /// Rende il comando sempre disponibile, anche nella schermata iniziale di Revit
    /// quando nessun documento è aperto (Zero-Doc State / Revit Home).
    /// </summary>
    public class ZeroDocAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return true;
        }
    }

    /// <summary>
    /// Rende il comando disponibile solo quando c'è almeno un documento aperto in Revit.
    /// </summary>
    public class ActiveDocAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            return applicationData.ActiveUIDocument?.Document != null;
        }
    }

    /// <summary>
    /// Rende il comando disponibile solo quando c'è un documento con Worksharing abilitato.
    /// </summary>
    public class ActiveWorksharedDocAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            var doc = applicationData.ActiveUIDocument?.Document;
            return doc != null && doc.IsWorkshared;
        }
    }
}
