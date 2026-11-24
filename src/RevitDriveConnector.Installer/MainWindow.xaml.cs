using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace RevitDriveConnector.Installer
{
    public partial class MainWindow : Window
    {
        private readonly List<string> _detectedRevitVersions = new();
        private readonly bool _isItalian;

        public MainWindow()
        {
            InitializeComponent();
            string osLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
            _isItalian = (osLang == "it");

            ApplyLocalization();
            Loaded += MainWindow_Loaded;
        }

        private void ApplyLocalization()
        {
            if (_isItalian)
            {
                TxtHeaderSubtitle.Text = "Worksharing multiutente e collaborazione cloud per Revit (2025 - 2027)";
                TxtSummaryTitle.Text = "Riepilogo Configurazione del Sistema";
                LblRevitStatus.Text = "Versione Revit rilevata:";
                LblDriveStatus.Text = "Google Drive rilevato:";
                LblInstallPath.Text = "Destinazione Plugin:";
                TxtFeaturesHeader.Text = "Funzionalita incluse nell'installazione:";
                TxtFeature1.Text = "• Scheda 'Drive Connector' nella barra multifunzione (Ribbon) di Revit";
                TxtFeature2.Text = "• Prevenzione automatica delle corruzioni di file e lock coordinato";
                TxtFeature3.Text = "• Prestito elementi e richieste di modifica istantanee stile BIM Collaborate Pro";
                TxtLog.Text = "Pronto per l'installazione.";
                BtnCancel.Content = "Esci";
                BtnInstall.Content = "Installa Plugin per Revit";
            }
            else
            {
                TxtHeaderSubtitle.Text = "Multi-user worksharing and cloud collaboration for Revit (2025 - 2027)";
                TxtSummaryTitle.Text = "System Configuration Summary";
                LblRevitStatus.Text = "Detected Revit version:";
                LblDriveStatus.Text = "Detected Google Drive:";
                LblInstallPath.Text = "Plugin Destination:";
                TxtFeaturesHeader.Text = "Features included in installation:";
                TxtFeature1.Text = "• 'Drive Connector' tab in Autodesk Revit Ribbon";
                TxtFeature2.Text = "• Automatic file corruption prevention and coordinated locking";
                TxtFeature3.Text = "• Real-time element borrowing and modification requests";
                TxtLog.Text = "Ready for installation.";
                BtnCancel.Content = "Exit";
                BtnInstall.Content = "Install Plugin for Revit";
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CheckEnvironment();
        }

        private void CheckEnvironment()
        {
            // 1. Rileva versioni di Revit
            _detectedRevitVersions.Clear();
            string[] possibleVersions = { "2027", "2026", "2025" };

            foreach (var v in possibleVersions)
            {
                string addinFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", v);
                string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", $"Revit {v}");
                if (Directory.Exists(addinFolder) || Directory.Exists(programFiles))
                {
                    _detectedRevitVersions.Add(v);
                }
            }

            if (_detectedRevitVersions.Count > 0)
            {
                TxtRevitStatus.Text = $"Revit {string.Join(", ", _detectedRevitVersions)}";
                TxtRevitStatus.Foreground = System.Windows.Media.Brushes.Green;
            }
            else
            {
                TxtRevitStatus.Text = _isItalian ? "Nessuna installazione standard rilevata" : "No standard installation detected";
                TxtRevitStatus.Foreground = System.Windows.Media.Brushes.Orange;
            }

            // 2. Modalità Serverless Google Drive OAuth2 API
            TxtDriveStatus.Text = _isItalian
                ? "Google Drive OAuth2 API (Personale & Workspace Shared Drives)"
                : "Google Drive OAuth2 API (Personal & Workspace Shared Drives)";
            TxtDriveStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (BtnInstall.Content.ToString() == (_isItalian ? "Completato! Chiudi" : "Completed! Close"))
            {
                Close();
                return;
            }

            BtnInstall.IsEnabled = false;
            PrgBar.Value = 10;
            TxtLog.Text = _isItalian ? "Estrazione file di programma in corso..." : "Extracting program files...";

            try
            {
                await Task.Run(() => PerformInstallation());

                PrgBar.Value = 100;
                TxtLog.Text = _isItalian ? "Installazione completata con successo!" : "Installation completed successfully!";
                TxtLog.Foreground = System.Windows.Media.Brushes.Green;

                BtnInstall.Content = _isItalian ? "Completato! Chiudi" : "Completed! Close";
                BtnInstall.Background = System.Windows.Media.Brushes.ForestGreen;
                BtnInstall.IsEnabled = true;

                string successMsg = _isItalian
                    ? "Google Drive Connector per Autodesk Revit è stato installato con successo!\n\n" +
                      "Ora puoi avviare Revit: troverai la nuova scheda 'Drive Connector' nella barra multifunzione (Ribbon)."
                    : "Google Drive Connector for Autodesk Revit was installed successfully!\n\n" +
                      "You can now launch Revit: you will find the new 'Drive Connector' tab on the Ribbon.";

                MessageBox.Show(
                    successMsg,
                    _isItalian ? "Installazione Completata" : "Installation Completed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                TxtLog.Text = $"{(_isItalian ? "Errore" : "Error")}: {ex.Message}";
                TxtLog.Foreground = System.Windows.Media.Brushes.Red;
                BtnInstall.IsEnabled = true;
                MessageBox.Show($"{(_isItalian ? "Si è verificato un errore durante l'installazione" : "An error occurred during installation")}:\n{ex.Message}",
                    _isItalian ? "Errore Setup" : "Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PerformInstallation()
        {
            // 0. Chiudi eventuali processi Daemon/Server in esecuzione
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.IndexOf("RevitDriveConnector.Daemon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        p.ProcessName.IndexOf("RevitDriveConnector.Server", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch { }
            }

            var revitProcesses = Process.GetProcessesByName("Revit");
            if (revitProcesses.Length > 0)
            {
                throw new InvalidOperationException(_isItalian
                    ? "Autodesk Revit è attualmente in esecuzione.\nPer favore chiudi Revit prima di procedere con l'installazione, in modo da poter aggiornare tutti i file del plugin."
                    : "Autodesk Revit is currently running.\nPlease close Revit before proceeding with the installation, so that all plugin files can be updated.");
            }

            // 1. Destinazione installazione: %APPDATA%\RevitDriveConnector\bin
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector");
            string installBinDir = Path.Combine(appData, "bin");
            Directory.CreateDirectory(installBinDir);

            // 2. Estrai risorsa payload.zip incorporata
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = "RevitDriveConnector.Installer.Resources.payload.zip";

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException($"Risorsa incorporata '{resourceName}' non trovata nell'eseguibile.");
                }

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string targetFile = Path.Combine(installBinDir, entry.FullName);
                    string? dir = Path.GetDirectoryName(targetFile);
                    if (dir != null) Directory.CreateDirectory(dir);

                    entry.ExtractToFile(targetFile, true);
                }
            }

            // Copia credentials.json anche nella root di %APPDATA%\RevitDriveConnector se non esiste già
            string extractedCreds = Path.Combine(installBinDir, "credentials.json");
            string rootCreds = Path.Combine(appData, "credentials.json");
            if (File.Exists(extractedCreds) && !File.Exists(rootCreds))
            {
                try { File.Copy(extractedCreds, rootCreds, false); } catch { }
            }

            // 3. Registra manifest .addin per Revit (seleziona build .NET 8 per 2025/2026 e .NET 10 per 2027+)
            var versionsToInstall = _detectedRevitVersions.Count > 0 ? _detectedRevitVersions : new List<string> { "2027", "2026", "2025" };

            foreach (var ver in versionsToInstall)
            {
                bool useNet8 = ver == "2025" || ver == "2026";
                string candidateSubfolder = Path.Combine(installBinDir, useNet8 ? "net8" : "net10", "RevitDriveConnector.Addin.dll");
                string addinDllPath = File.Exists(candidateSubfolder)
                    ? candidateSubfolder
                    : Path.Combine(installBinDir, "RevitDriveConnector.Addin.dll");

                string addinFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", ver);
                Directory.CreateDirectory(addinFolder);
                string manifestFile = Path.Combine(addinFolder, "RevitDriveConnector.addin");

                string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>Google Drive Connector for Revit {ver}</Name>
    <Assembly>{addinDllPath}</Assembly>
    <AddInId>8B12F0C1-6028-44A5-B7D4-1B155B907E92</AddInId>
    <FullClassName>RevitDriveConnector.Addin.App</FullClassName>
    <VendorId>REVIT_DRIVE_CONNECTOR</VendorId>
    <VendorDescription>OpenCloud Worksharing Connector for Autodesk Revit</VendorDescription>
    <UseRevitContext>false</UseRevitContext>
  </AddIn>
</RevitAddIns>";

                File.WriteAllText(manifestFile, xml);
            }

            // 4. Configurazione Cache Locale
            string cachePath = @"C:\RevitDriveCache";
            Directory.CreateDirectory(cachePath);

            // 5. Scrivi config.json solo se non esiste già (preserva eventuali impostazioni personalizzate in aggiornamento)
            string configFile = Path.Combine(appData, "config.json");
            if (!File.Exists(configFile))
            {
                var configObj = new
                {
                    localCachePath = cachePath,
                    syncTimeoutSeconds = 120,
                    language = "Auto",
                    enableLiveElementLocks = true,
                    uploadChunkSizeMb = 8
                };

                File.WriteAllText(configFile, JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
