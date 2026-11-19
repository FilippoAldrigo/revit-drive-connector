using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
    public partial class OpenProjectWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly NamedPipeIpcClient _ipcClient = new();
        private bool _isGoogleConnected = false;
        private bool _promptedSignIn = false;
        public ObservableCollection<CloudProjectItem> Projects { get; } = new();
        public CloudProjectItem? SelectedProject { get; private set; }

        public OpenProjectWindow(UIApplication uiapp)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _uiapp = uiapp;
            LvProjects.ItemsSource = Projects;

            ApplyLocalization();

            Loaded += async (s, e) =>
            {
                await RefreshAccountStatusAsync();
                await LoadProjectsAsync();
            };
        }

        private void ApplyLocalization()
        {
            Title = Loc.Tr("Open_Title");
            TxtHeaderTitle.Text = Loc.Tr("Open_Header");
            TxtHeaderSubtitle.Text = Loc.Tr("Open_Subtitle");
            TxtAccountBadge.Text = Loc.Tr("Open_NoAccount");
            BtnManageAccount.Content = Loc.Tr("Open_Manage");

            ColName.Header = Loc.Tr("Open_ColName");
            ColSource.Header = Loc.Tr("Open_ColSource");
            ColStatus.Header = Loc.Tr("Open_ColStatus");
            ColVersion.Header = Loc.Tr("Open_ColVersion");
            ColModified.Header = Loc.Tr("Open_ColModified");

            BtnRefresh.Content = Loc.Tr("Open_Refresh");
            BtnShare.Content = Loc.Tr("Open_Collaborators");
            BtnCancel.Content = Loc.Tr("Open_Cancel");
            BtnOpen.Content = Loc.Tr("Open_OpenLocal");
            ChkCreateNewLocal.Content = Loc.Tr("Open_ChkCreateNewLocal");
        }

        private async Task RefreshAccountStatusAsync()
        {
            _isGoogleConnected = false;
            try
            {
                var resp = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.GetGoogleAccountStatus
                }, timeoutMs: 10000);

                if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                {
                    var acct = JsonSerializer.Deserialize<GoogleAccountInfo>(resp.DataJson);
                    if (acct != null && acct.IsConnected)
                    {
                        _isGoogleConnected = true;
                        TxtAccountBadge.Text = acct.Email;
                        TxtAccountBadge.Foreground = System.Windows.Media.Brushes.ForestGreen;
                        return;
                    }
                }
            }
            catch
            {
            }

            TxtAccountBadge.Text = Loc.Tr("Open_NoAccount");
            TxtAccountBadge.Foreground = System.Windows.Media.Brushes.Crimson;
        }

        private async void BtnManageAccount_Click(object sender, RoutedEventArgs e)
        {
            var win = new GoogleAccountWindow();
            win.ShowDialog();
            await RefreshAccountStatusAsync();
            await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            TxtStatus.Text = Loc.Tr("Open_Searching");
            TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 132, 199));
            Projects.Clear();
            BtnOpen.IsEnabled = false;

            try
            {
                var resp = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.ListCloudProjects
                }, timeoutMs: 30000);

                // Ricontrolla lo stato account nel caso ListCloudProjects abbia rilevato e pulito un token scaduto
                await RefreshAccountStatusAsync();

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {resp.ErrorMessage ?? "No response from daemon"}";
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;
                    return;
                }

                var list = JsonSerializer.Deserialize<List<CloudProjectItem>>(resp.DataJson);
                if (list != null && list.Count > 0)
                {
                    foreach (var item in list)
                    {
                        Projects.Add(item);
                    }

                    if (_isGoogleConnected)
                    {
                        TxtStatus.Text = Loc.Format("Open_FoundCount", list.Count);
                    }
                    else
                    {
                        TxtStatus.Text = Loc.Format("Open_FoundLocalOnly", list.Count);
                        TxtStatus.Foreground = System.Windows.Media.Brushes.DarkOrange;
                    }
                }
                else if (!_isGoogleConnected)
                {
                    TxtStatus.Text = Loc.Tr("Open_ConnectRequired");
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;

                    if (!_promptedSignIn)
                    {
                        _promptedSignIn = true;
                        var ask = MessageBox.Show(
                            Loc.Tr("Open_ConnectPrompt"),
                            Loc.Tr("Open_Title"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (ask == MessageBoxResult.Yes)
                        {
                            var win = new GoogleAccountWindow();
                            win.ShowDialog();
                            await RefreshAccountStatusAsync();
                            if (_isGoogleConnected)
                            {
                                await LoadProjectsAsync();
                            }
                        }
                    }
                }
                else
                {
                    TxtStatus.Text = Loc.Tr("Open_NoProjects");
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;
            }
        }

        private void LvProjects_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedProject = LvProjects.SelectedItem as CloudProjectItem;
            BtnOpen.IsEnabled = SelectedProject != null;
            BtnShare.IsEnabled = SelectedProject != null;
            if (SelectedProject != null)
            {
                BtnOpen.Content = SelectedProject.IsMountedLocally ? Loc.Tr("Open_OpenLocal") : Loc.Tr("Open_SyncAndOpen");
            }
        }

        private void BtnShare_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProject == null) return;
            var shareWin = new ShareProjectWindow(SelectedProject.Name);
            shareWin.ShowDialog();
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAccountStatusAsync();
            await LoadProjectsAsync();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedProject == null) return;

            BtnOpen.IsEnabled = false;
            TxtStatus.Text = Loc.Format("Open_Aligning", SelectedProject.Name);

            try
            {
                IpcResponse resp;
                if (!SelectedProject.IsMountedLocally)
                {
                    TxtStatus.Text = Loc.Format("Open_SyncingCloud", SelectedProject.Name);
                    var mountReq = new
                    {
                        ProjectName = SelectedProject.Name,
                        CloudFolderId = SelectedProject.CloudFolderId
                    };

                    resp = await _ipcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.MountCloudProject,
                        PayloadJson = JsonSerializer.Serialize(mountReq)
                    }, timeoutMs: 180000);
                }
                else
                {
                    var prepReq = new
                    {
                        ProjectId = SelectedProject.Id,
                        ProjectName = SelectedProject.Name,
                        User = _uiapp.Application.Username
                    };

                    resp = await _ipcClient.SendRequestAsync(new IpcRequest
                    {
                        Command = IpcCommand.DownloadAndPrepareProject,
                        PayloadJson = JsonSerializer.Serialize(prepReq)
                    }, timeoutMs: 300000);
                }

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {resp.ErrorMessage}";
                    BtnOpen.IsEnabled = true;
                    return;
                }

                var prepResult = JsonSerializer.Deserialize<Dictionary<string, string>>(resp.DataJson);
                string centralPath = prepResult?["CentralPath"] ?? "";

                if (!File.Exists(centralPath))
                {
                    TxtStatus.Text = Loc.Tr("Open_CentralNotFound");
                    BtnOpen.IsEnabled = true;
                    return;
                }

                string projectDir = Path.GetDirectoryName(Path.GetDirectoryName(centralPath)!)!;
                string localDir = Path.Combine(projectDir, "Local");
                Directory.CreateDirectory(localDir);

                string username = _uiapp.Application.Username;
                string localFilePath = Path.Combine(localDir, $"{SelectedProject.Name}_{username}.rvt");

                var centralModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(centralPath);
                var localModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(localFilePath);

                // 1. Se il file locale è già aperto in Revit, portiamolo in primo piano ed eseguiamo ReloadLatest
                foreach (Autodesk.Revit.DB.Document openDoc in _uiapp.Application.Documents)
                {
                    if (string.Equals(openDoc.PathName, localFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        Autodesk.Revit.UI.TaskDialog.Show(Loc.Tr("Ribbon_TabName"), Loc.Tr("Open_LocalAlreadyOpen"));
                        try
                        {
                            openDoc.ReloadLatest(new Autodesk.Revit.DB.ReloadLatestOptions());
                        }
                        catch { }
                        DialogResult = true;
                        Close();
                        return;
                    }
                }

                // 2. Verifica che il modello centrale abbia il worksharing attivo (senza mai ri-salvare come centrale un modello già workshared,
                //    altrimenti si altera la corrispondenza degli episodi delle copie locali dei collaboratori)
                try
                {
                    var centralInfo = BasicFileInfo.Extract(centralPath);
                    if (!centralInfo.IsWorkshared)
                    {
                        TxtStatus.Text = Loc.Tr("Gatekeeper_Subtitle");
                        var openOpts = new OpenOptions();
                        var tempDoc = _uiapp.Application.OpenDocumentFile(centralModelPath, openOpts);
                        if (!tempDoc.IsWorkshared)
                        {
                            tempDoc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                        }
                        var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };
                        var wsOpts = new WorksharingSaveAsOptions { SaveAsCentral = true };
                        saveOpts.SetWorksharingOptions(wsOpts);
                        tempDoc.SaveAs(centralPath, saveOpts);
                        try
                        {
                            var relOpts = new RelinquishOptions(true)
                            {
                                StandardWorksets = true,
                                ViewWorksets = true,
                                FamilyWorksets = true,
                                UserWorksets = true,
                                CheckedOutElements = true
                            };
                            WorksharingUtils.RelinquishOwnership(tempDoc, relOpts, new TransactWithCentralOptions());
                        }
                        catch { }
                        tempDoc.Close(false);

                        await _ipcClient.SendRequestAsync(new IpcRequest
                        {
                            Command = IpcCommand.UploadProjectChanges,
                            PayloadJson = JsonSerializer.Serialize(new
                            {
                                ProjectId = SelectedProject.Name,
                                CentralPath = centralPath,
                                LockToken = "",
                                User = username
                            })
                        }, timeoutMs: 180000);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OpenProjectWindow] Central check warning: {ex.Message}");
                }

                string localBackupDir = Path.Combine(localDir, $"{SelectedProject.Name}_{username}_backup");
                bool shouldCreateNew = ChkCreateNewLocal.IsChecked == true || !File.Exists(localFilePath);

                if (shouldCreateNew)
                {
                    SafeDeleteFileAndDirectory(localFilePath, localBackupDir);

                    int suffix = 1;
                    while (File.Exists(localFilePath) || Directory.Exists(localBackupDir))
                    {
                        localFilePath = Path.Combine(localDir, $"{SelectedProject.Name}_{username}_{suffix}.rvt");
                        localBackupDir = Path.Combine(localDir, $"{SelectedProject.Name}_{username}_{suffix}_backup");
                        localModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(localFilePath);
                        suffix++;
                    }

                    WorksharingUtils.CreateNewLocal(centralModelPath, localModelPath);
                }
                else
                {
                    try
                    {
                        var localInfo = BasicFileInfo.Extract(localFilePath);
                        if (!localInfo.IsWorkshared || !string.Equals(localInfo.CentralPath, centralPath, StringComparison.OrdinalIgnoreCase))
                        {
                            SafeDeleteFileAndDirectory(localFilePath, localBackupDir);
                            WorksharingUtils.CreateNewLocal(centralModelPath, localModelPath);
                            shouldCreateNew = true;
                        }
                    }
                    catch
                    {
                        SafeDeleteFileAndDirectory(localFilePath, localBackupDir);
                        WorksharingUtils.CreateNewLocal(centralModelPath, localModelPath);
                        shouldCreateNew = true;
                    }
                }

                var finalOpenOpts = new OpenOptions
                {
                    DetachFromCentralOption = DetachFromCentralOption.DoNotDetach
                };

                SynchronizationCoordinator.ActiveProjectId = SelectedProject.Name;
                SynchronizationCoordinator.SuppressNextAutoReload = true;
                DialogResult = true;
                Close();

                RecentFilesService.RegisterInRevitRecentFiles(_uiapp.Application.VersionNumber, localFilePath);
                _uiapp.OpenAndActivateDocument(localModelPath, finalOpenOpts, false);

                if (!shouldCreateNew)
                {
                    try
                    {
                        var openedDoc = _uiapp.ActiveUIDocument?.Document;
                        if (openedDoc != null && openedDoc.IsWorkshared)
                        {
                            openedDoc.ReloadLatest(new Autodesk.Revit.DB.ReloadLatestOptions());
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
                BtnOpen.IsEnabled = true;
            }
        }

        private static void SafeDeleteFileAndDirectory(string filePath, string dirPath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(dirPath))
                {
                    var di = new DirectoryInfo(dirPath);
                    foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try { fi.Attributes = FileAttributes.Normal; } catch { }
                    }
                    di.Delete(true);
                }
            }
            catch { }
        }
    }
}
