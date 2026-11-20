using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Commands;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Addin.UI;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class App : IExternalApplication
    {
        public static App? Instance { get; private set; }
        public static UIControlledApplication? UiControlledApp { get; private set; }
        public static System.Windows.Threading.Dispatcher? UiDispatcher { get; private set; }
        private static readonly NamedPipeIpcClient IpcClient = new();

        public Result OnStartup(UIControlledApplication application)
        {
            Instance = this;
            UiControlledApp = application;
            UiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            try
            {
                var cfg = AppConfig.Load();
                Loc.Initialize(cfg.Language, application.ControlledApplication.Language.ToString());

                // Inizializza ExternalEvent per rilascio permessi da notifiche Toast modeless
                BorrowNotificationService.InitializeExternalEvent();

                // Inizializza motore di blocco live per elementi in modifica prima del sync
                LiveElementLockService.Initialize(application);

                // Inizializza la Ribbon UI
                CreateRibbonTabAndButtons(application);

                // Registra gli handler per gli eventi di sincronizzazione e ricarica
                application.ControlledApplication.DocumentSynchronizingWithCentral += SynchronizationCoordinator.OnDocumentSynchronizingWithCentral;
                application.ControlledApplication.DocumentSynchronizedWithCentral += SynchronizationCoordinator.OnDocumentSynchronizedWithCentral;
                application.ControlledApplication.DocumentReloadingLatest += SynchronizationCoordinator.OnDocumentReloadingLatest;
                application.ControlledApplication.DocumentReloadedLatest += OnDocumentReloadedLatest;
                application.ControlledApplication.DocumentChanged += OnDocumentChanged;
                application.ControlledApplication.DocumentOpened += OnDocumentOpened;
                application.ControlledApplication.DocumentClosing += SynchronizationCoordinator.OnDocumentClosing;

                // Registra Idling per notifiche push "Live Toast" delle richieste di prestito
                application.Idling += OnIdling;

                // Auto-avvio dei servizi di sfondo se non già in esecuzione
                NamedPipeIpcClient.EnsureDaemonRunningHandler = EnsureDaemonRunningAsync;
                _ = Task.Run(EnsureDaemonRunningAsync);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Drive Connector Error", $"Errore di avvio del plugin Drive Connector: {ex.Message}");
                return Result.Failed;
            }
        }

        private static bool _pendingAutoReload = false;
        private static Document? _pendingDocToReload = null;

        private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e)
        {
            SynchronizationCoordinator.OnDocumentOpened(sender, e);
            if (e.Document != null && e.Document.IsWorkshared)
            {
                LiveElementLockService.OnDocumentOpened(e.Document);

                string centralPath = SynchronizationCoordinator.GetCentralModelPath(e.Document);
                bool isDriveManaged = SynchronizationCoordinator.IsDriveManagedPath(e.Document.PathName) ||
                                      SynchronizationCoordinator.IsDriveManagedPath(centralPath);

                if (isDriveManaged)
                {
                    // Auto-risoluzione dei collegamenti mancanti cercando nella cartella Links di Google Drive
                    ExternalLinkManagerService.AutoHealMissingLinks(e.Document);
                }

                // Se è un modello locale gestito da Drive Connector, pianifichiamo un allineamento cloud + ReloadLatest su Idling
                if (!e.Document.IsDetached && SynchronizationCoordinator.IsDriveManagedPath(e.Document.PathName))
                {
                    if (SynchronizationCoordinator.SuppressNextAutoReload)
                    {
                        SynchronizationCoordinator.SuppressNextAutoReload = false;
                    }
                    else
                    {
                        _pendingDocToReload = e.Document;
                        _pendingAutoReload = true;
                    }
                }
            }
        }

        private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
        {
            LiveElementLockService.OnDocumentChanged(e);
        }

        private void OnDocumentReloadedLatest(object? sender, DocumentReloadedLatestEventArgs e)
        {
            LiveElementLockService.IsSyncOrReloadActive = false;
            if (e.Document != null && e.Document.IsWorkshared)
            {
                string centralPath = SynchronizationCoordinator.GetCentralModelPath(e.Document);
                if (SynchronizationCoordinator.IsDriveManagedPath(centralPath))
                {
                    string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
                    LiveElementLockService.SeedElementLookupCache(e.Document, projectId);
                }
            }
        }

        private void OnIdling(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (sender is UIApplication uiapp)
            {
                if (_pendingAutoReload && _pendingDocToReload != null && _pendingDocToReload.IsValidObject)
                {
                    _pendingAutoReload = false;
                    var doc = _pendingDocToReload;
                    _pendingDocToReload = null;

                    try
                    {
                        if (doc.IsWorkshared && !doc.IsDetached)
                        {
                            // Allinea prima il modello centrale nella cache locale scaricando eventuali novità da Google Drive
                            bool aligned = SynchronizationCoordinator.AlignCentralFromCloudSync(doc);
                            if (aligned)
                            {
                                try
                                {
                                    SynchronizationCoordinator.SuppressNextReloadAlignment = true;
                                    LiveElementLockService.IsSyncOrReloadActive = true;
                                    doc.ReloadLatest(new ReloadLatestOptions());
                                }
                                finally
                                {
                                    SynchronizationCoordinator.SuppressNextReloadAlignment = false;
                                    LiveElementLockService.IsSyncOrReloadActive = false;
                                }
                            }
                            else
                            {
                                System.Windows.MessageBox.Show(
                                    Loc.Tr("Open_LocalIncompatibleNotice"),
                                    Loc.Tr("Ribbon_TabName"),
                                    System.Windows.MessageBoxButton.OK,
                                    System.Windows.MessageBoxImage.Warning);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[App] Auto ReloadLatest notice: {ex.Message}");
                        System.Windows.MessageBox.Show(
                            $"{Loc.Tr("Open_LocalIncompatibleNotice")}\n\nDettaglio: {ex.Message}",
                            Loc.Tr("Ribbon_TabName"),
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                }

                BorrowNotificationService.OnIdlingCheck(uiapp);
                LiveElementLockService.OnIdlingTick(uiapp);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentSynchronizingWithCentral -= SynchronizationCoordinator.OnDocumentSynchronizingWithCentral;
                application.ControlledApplication.DocumentSynchronizedWithCentral -= SynchronizationCoordinator.OnDocumentSynchronizedWithCentral;
                application.ControlledApplication.DocumentReloadingLatest -= SynchronizationCoordinator.OnDocumentReloadingLatest;
                application.ControlledApplication.DocumentReloadedLatest -= OnDocumentReloadedLatest;
                application.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                application.ControlledApplication.DocumentOpened -= OnDocumentOpened;
                application.ControlledApplication.DocumentClosing -= SynchronizationCoordinator.OnDocumentClosing;

                application.Idling -= OnIdling;
                BorrowNotificationService.ClearSession();
                LiveElementLockService.Shutdown();

                return Result.Succeeded;
            }
            catch
            {
                return Result.Failed;
            }
        }

        public static string? FindDaemonExe()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var candidates = new List<string>();

            if (!string.IsNullOrEmpty(assemblyDir))
            {
                candidates.Add(Path.Combine(assemblyDir, "RevitDriveConnector.Daemon.exe"));
            }

            string appDataBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDriveConnector", "bin", "RevitDriveConnector.Daemon.exe");
            candidates.Add(appDataBin);

            string? dir = assemblyDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                candidates.Add(Path.Combine(dir, "dist", "payload", "bin", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "src", "RevitDriveConnector.Daemon", "bin", "Release", "net8.0", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "src", "RevitDriveConnector.Daemon", "bin", "Debug", "net8.0", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "src", "RevitDriveConnector.Daemon", "bin", "Release", "net10.0", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "src", "RevitDriveConnector.Daemon", "bin", "Debug", "net10.0", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "RevitDriveConnector.Daemon", "bin", "Release", "net8.0", "RevitDriveConnector.Daemon.exe"));
                candidates.Add(Path.Combine(dir, "RevitDriveConnector.Daemon", "bin", "Debug", "net8.0", "RevitDriveConnector.Daemon.exe"));
                dir = Path.GetDirectoryName(dir);
            }

            return candidates.FirstOrDefault(File.Exists);
        }

        public static async Task EnsureDaemonRunningAsync()
        {
            if (await IpcClient.PingServerAsync(800))
            {
                return;
            }

            try
            {
                string? daemonExe = FindDaemonExe();
                if (!string.IsNullOrEmpty(daemonExe) && File.Exists(daemonExe))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = daemonExe,
                        WorkingDirectory = Path.GetDirectoryName(daemonExe) ?? "",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi);

                    for (int i = 0; i < 16; i++)
                    {
                        await Task.Delay(500);
                        if (await IpcClient.PingServerAsync(500))
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[App] Errore avvio Daemon: {ex.Message}");
            }
        }

        private void CreateRibbonTabAndButtons(UIControlledApplication application)
        {
            string tabName = Loc.Tr("Ribbon_TabName");
            try
            {
                application.CreateRibbonTab(tabName);
            }
            catch
            {
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Pannello 1: Gestione Progetti Cloud
            RibbonPanel projectPanel = application.CreateRibbonPanel(tabName, Loc.Tr("Panel_Projects"));

            var btnOpenData = new PushButtonData(
                "btnOpenCloudProject",
                Loc.Tr("Btn_Open"),
                assemblyPath,
                typeof(OpenCloudProjectCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Open_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_open_32.png"),
                Image = IconHelper.GetBitmap("icon_open_16.png"),
                AvailabilityClassName = typeof(ZeroDocAvailability).FullName
            };
            projectPanel.AddItem(btnOpenData);

            var btnPublishData = new PushButtonData(
                "btnPublishToDrive",
                Loc.Tr("Btn_Publish"),
                assemblyPath,
                typeof(PublishToDriveCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Publish_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_publish_32.png"),
                Image = IconHelper.GetBitmap("icon_publish_16.png"),
                AvailabilityClassName = typeof(ActiveDocAvailability).FullName
            };
            projectPanel.AddItem(btnPublishData);

            var btnShareData = new PushButtonData(
                "btnShareProject",
                Loc.Tr("Btn_Share"),
                assemblyPath,
                typeof(ShareProjectCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Share_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_share_32.png"),
                Image = IconHelper.GetBitmap("icon_share_16.png"),
                AvailabilityClassName = typeof(ZeroDocAvailability).FullName
            };
            projectPanel.AddItem(btnShareData);

            // Pannello 2: Sincronizzazione Intelligente
            RibbonPanel syncPanel = application.CreateRibbonPanel(tabName, Loc.Tr("Panel_Sync"));

            var btnSyncData = new PushButtonData(
                "btnSyncDrive",
                Loc.Tr("Btn_Sync"),
                assemblyPath,
                typeof(SyncWithDriveCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Sync_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_sync_32.png"),
                Image = IconHelper.GetBitmap("icon_sync_16.png"),
                AvailabilityClassName = typeof(ActiveWorksharedDocAvailability).FullName
            };
            syncPanel.AddItem(btnSyncData);

            var btnStatusData = new PushButtonData(
                "btnCloudStatus",
                Loc.Tr("Btn_Status"),
                assemblyPath,
                typeof(CloudStatusCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Status_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_status_32.png"),
                Image = IconHelper.GetBitmap("icon_status_16.png"),
                AvailabilityClassName = typeof(ActiveWorksharedDocAvailability).FullName
            };
            syncPanel.AddItem(btnStatusData);

            // Pannello 3: Prestiti e Modifiche (Stile BIM Collaborate Pro)
            RibbonPanel borrowPanel = application.CreateRibbonPanel(tabName, Loc.Tr("Panel_Borrow"));

            var btnRequestBorrowData = new PushButtonData(
                "btnRequestBorrow",
                Loc.Tr("Btn_RequestBorrow"),
                assemblyPath,
                typeof(RequestBorrowCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_RequestBorrow_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_borrow_req_32.png"),
                Image = IconHelper.GetBitmap("icon_borrow_req_16.png"),
                AvailabilityClassName = typeof(ActiveWorksharedDocAvailability).FullName
            };
            borrowPanel.AddItem(btnRequestBorrowData);

            var btnBorrowCenterData = new PushButtonData(
                "btnBorrowCenter",
                Loc.Tr("Btn_BorrowCenter"),
                assemblyPath,
                typeof(BorrowCenterCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_BorrowCenter_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_borrow_center_32.png"),
                Image = IconHelper.GetBitmap("icon_borrow_center_16.png"),
                AvailabilityClassName = typeof(ActiveWorksharedDocAvailability).FullName
            };
            borrowPanel.AddItem(btnBorrowCenterData);

            // Pannello 4: Collegamenti Cloud (Revit, DWG, IFC)
            RibbonPanel linksPanel = application.CreateRibbonPanel(tabName, Loc.Tr("Panel_Links"));
            var btnManageLinksData = new PushButtonData(
                "btnManageLinks",
                Loc.Tr("Btn_ManageLinks"),
                assemblyPath,
                typeof(ManageLinksCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_ManageLinks_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_links_32.png"),
                Image = IconHelper.GetBitmap("icon_links_16.png"),
                AvailabilityClassName = typeof(ActiveDocAvailability).FullName
            };
            linksPanel.AddItem(btnManageLinksData);

            // Pannello 5: Configurazione
            RibbonPanel configPanel = application.CreateRibbonPanel(tabName, Loc.Tr("Panel_Config"));

            var btnAccountData = new PushButtonData(
                "btnGoogleAccount",
                Loc.Tr("Btn_Account"),
                assemblyPath,
                typeof(GoogleAccountCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Account_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_account_32.png"),
                Image = IconHelper.GetBitmap("icon_account_16.png"),
                AvailabilityClassName = typeof(ZeroDocAvailability).FullName
            };
            configPanel.AddItem(btnAccountData);

            var btnSettingsData = new PushButtonData(
                "btnCloudSettings",
                Loc.Tr("Btn_Settings"),
                assemblyPath,
                typeof(SettingsCommand).FullName)
            {
                ToolTip = Loc.Tr("Btn_Settings_Tip"),
                LargeImage = IconHelper.GetBitmap("icon_settings_32.png"),
                Image = IconHelper.GetBitmap("icon_settings_16.png"),
                AvailabilityClassName = typeof(ZeroDocAvailability).FullName
            };
            configPanel.AddItem(btnSettingsData);
        }
    }
}
