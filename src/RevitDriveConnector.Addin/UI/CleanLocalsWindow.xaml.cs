using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
    public class LocalCopyItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string ProjectName { get; set; } = string.Empty;
        public string ProjectDir { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? RvtFilePath { get; set; }
        public string? BackupDirPath { get; set; }
        public long SizeBytes { get; set; }
        public string SizeDisplay { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public bool IsOpenInRevit { get; set; }
        public bool IsCentralCacheOnly { get; set; }
        public bool IsCloudOnly { get; set; }
        public bool CanSelect => !IsOpenInRevit && !IsCloudOnly;
        public string StatusDisplay { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public partial class CleanLocalsWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly NamedPipeIpcClient _ipcClient = new();
        private readonly string _cacheRoot;
        public ObservableCollection<LocalCopyItem> LocalItems { get; } = new();

        public CleanLocalsWindow(UIApplication uiapp)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _uiapp = uiapp;

            var cfg = AppConfig.Load();
            _cacheRoot = !string.IsNullOrWhiteSpace(cfg.LocalCachePath) ? cfg.LocalCachePath : @"C:\RevitDriveCache";

            LvLocals.ItemsSource = LocalItems;
            ApplyLocalization();
            ScanLocalCopies();

            Loaded += async (s, e) => await AppendCloudOnlyProjectsAsync();
        }

        private void ApplyLocalization()
        {
            Title = Loc.Tr("Clean_Title");
            TxtHeaderTitle.Text = Loc.Tr("Clean_Header");
            TxtHeaderSubtitle.Text = Loc.Tr("Clean_Subtitle");

            ColProject.Header = Loc.Tr("Clean_ColProject");
            ColFile.Header = Loc.Tr("Clean_ColFile");
            ColSize.Header = Loc.Tr("Clean_ColSize");
            ColModified.Header = Loc.Tr("Clean_ColModified");
            ColStatus.Header = Loc.Tr("Clean_ColStatus");

            ChkAlsoCleanCentral.Content = Loc.Tr("Clean_ChkAlsoCleanCentral");
            BtnSelectAll.Content = Loc.Tr("Clean_SelectAll");
            BtnDeselectAll.Content = Loc.Tr("Clean_DeselectAll");
            BtnRefresh.Content = Loc.Tr("Clean_Refresh");
            BtnClose.Content = Loc.Tr("Common_Close");
            BtnDeleteSelected.Content = Loc.Tr("Clean_BtnDeleteSelected");
        }

        private HashSet<string> GetOpenDocumentPaths()
        {
            var openPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Document doc in _uiapp.Application.Documents)
                {
                    if (!string.IsNullOrWhiteSpace(doc.PathName))
                    {
                        try { openPaths.Add(Path.GetFullPath(doc.PathName)); } catch { }
                    }
                }
            }
            catch { }
            return openPaths;
        }

        private bool IsProjectOpenInRevit(string projectDir)
        {
            try
            {
                string fullProjectDir = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (Document doc in _uiapp.Application.Documents)
                {
                    if (!string.IsNullOrWhiteSpace(doc.PathName))
                    {
                        try
                        {
                            string docFull = Path.GetFullPath(doc.PathName);
                            if (docFull.StartsWith(fullProjectDir, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch { }
                    }

                    if (doc.IsWorkshared)
                    {
                        string centralPath = SynchronizationCoordinator.GetCentralModelPath(doc);
                        if (!string.IsNullOrWhiteSpace(centralPath))
                        {
                            try
                            {
                                string centralFull = Path.GetFullPath(centralPath);
                                if (centralFull.StartsWith(fullProjectDir, StringComparison.OrdinalIgnoreCase))
                                    return true;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private void ScanLocalCopies()
        {
            LocalItems.Clear();

            if (!Directory.Exists(_cacheRoot))
            {
                TxtStatus.Text = Loc.Format("Clean_NoLocalsFound", _cacheRoot);
                TxtStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
                BtnDeleteSelected.IsEnabled = false;
                return;
            }

            var openPaths = GetOpenDocumentPaths();

            foreach (var projectDir in Directory.GetDirectories(_cacheRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string projectName = Path.GetFileName(projectDir);
                if (string.IsNullOrWhiteSpace(projectName) ||
                    projectName.StartsWith(".") ||
                    projectName.Equals("Transition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int addedForProject = 0;

                string localDir = Path.Combine(projectDir, "Local");
                if (Directory.Exists(localDir))
                {
                    var pairedBackupDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var rvtFile in Directory.GetFiles(localDir, "*.rvt").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var fi = new FileInfo(rvtFile);
                        string baseName = Path.GetFileNameWithoutExtension(rvtFile);
                        string candidateBackup = Path.Combine(localDir, baseName + "_backup");
                        string? backupDir = Directory.Exists(candidateBackup) ? candidateBackup : null;
                        if (backupDir != null)
                        {
                            pairedBackupDirs.Add(backupDir);
                        }

                        long itemSize = fi.Length + (backupDir != null ? ComputeDirectorySize(backupDir) : 0);

                        bool isOpen = false;
                        try { isOpen = openPaths.Contains(Path.GetFullPath(rvtFile)); } catch { }

                        LocalItems.Add(new LocalCopyItem
                        {
                            ProjectName = projectName,
                            ProjectDir = projectDir,
                            DisplayName = fi.Name,
                            RvtFilePath = rvtFile,
                            BackupDirPath = backupDir,
                            SizeBytes = itemSize,
                            SizeDisplay = FormatSize(itemSize),
                            LastModified = fi.LastWriteTime,
                            IsOpenInRevit = isOpen,
                            IsSelected = !isOpen,
                            StatusDisplay = isOpen ? Loc.Tr("Clean_StatusOpenInRevit") : Loc.Tr("Clean_StatusReady")
                        });
                        addedForProject++;
                    }

                    // Eventuali cartelle _backup orfane rimaste in Local senza file .rvt
                    foreach (var subDir in Directory.GetDirectories(localDir, "*_backup").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        if (pairedBackupDirs.Contains(subDir)) continue;
                        var di = new DirectoryInfo(subDir);
                        long dirSize = ComputeDirectorySize(subDir);

                        LocalItems.Add(new LocalCopyItem
                        {
                            ProjectName = projectName,
                            ProjectDir = projectDir,
                            DisplayName = di.Name + " (backup)",
                            RvtFilePath = null,
                            BackupDirPath = subDir,
                            SizeBytes = dirSize,
                            SizeDisplay = FormatSize(dirSize),
                            LastModified = di.LastWriteTime,
                            IsOpenInRevit = false,
                            IsSelected = true,
                            StatusDisplay = Loc.Tr("Clean_StatusReady")
                        });
                        addedForProject++;
                    }
                }

                // Eventuali file .rvt locali finiti nella radice del progetto (fuori da Central)
                foreach (var strayRvt in Directory.GetFiles(projectDir, "*.rvt").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(strayRvt);
                    long itemSize = fi.Length;

                    bool isOpen = false;
                    try { isOpen = openPaths.Contains(Path.GetFullPath(strayRvt)); } catch { }

                    LocalItems.Add(new LocalCopyItem
                    {
                        ProjectName = projectName,
                        ProjectDir = projectDir,
                        DisplayName = fi.Name,
                        RvtFilePath = strayRvt,
                        BackupDirPath = null,
                        SizeBytes = itemSize,
                        SizeDisplay = FormatSize(itemSize),
                        LastModified = fi.LastWriteTime,
                        IsOpenInRevit = isOpen,
                        IsSelected = !isOpen,
                        StatusDisplay = isOpen ? Loc.Tr("Clean_StatusOpenInRevit") : Loc.Tr("Clean_StatusReady")
                    });
                    addedForProject++;
                }

                // Se la cartella progetto esiste in cache ma non ha copie in Local (es. solo cache Central/manifest), mostrala comunque
                if (addedForProject == 0)
                {
                    long cacheSize = ComputeDirectorySize(projectDir);
                    bool projOpen = IsProjectOpenInRevit(projectDir);
                    var di = new DirectoryInfo(projectDir);

                    LocalItems.Add(new LocalCopyItem
                    {
                        ProjectName = projectName,
                        ProjectDir = projectDir,
                        DisplayName = Loc.Tr("Clean_CentralCacheOnly"),
                        RvtFilePath = null,
                        BackupDirPath = null,
                        SizeBytes = cacheSize,
                        SizeDisplay = FormatSize(cacheSize),
                        LastModified = di.LastWriteTime,
                        IsOpenInRevit = projOpen,
                        IsCentralCacheOnly = true,
                        IsSelected = !projOpen,
                        StatusDisplay = projOpen ? Loc.Tr("Clean_StatusOpenInRevit") : Loc.Tr("Clean_StatusReady")
                    });
                }
            }

            UpdateSummaryStatus();
        }

        private async Task AppendCloudOnlyProjectsAsync()
        {
            try
            {
                var resp = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.ListCloudProjects
                }, timeoutMs: 20000);

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson)) return;

                var cloudProjects = JsonSerializer.Deserialize<List<CloudProjectItem>>(resp.DataJson);
                if (cloudProjects == null || cloudProjects.Count == 0) return;

                var existingProjects = new HashSet<string>(
                    LocalItems.Select(i => i.ProjectName),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var cp in cloudProjects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(cp.Name) || existingProjects.Contains(cp.Name))
                    {
                        continue;
                    }

                    existingProjects.Add(cp.Name);
                    LocalItems.Add(new LocalCopyItem
                    {
                        ProjectName = cp.Name,
                        ProjectDir = string.Empty,
                        DisplayName = Loc.Tr("Clean_CloudOnlyFile"),
                        SizeBytes = 0,
                        SizeDisplay = "0 B",
                        LastModified = cp.LastModifiedLocal,
                        IsCloudOnly = true,
                        IsSelected = false,
                        StatusDisplay = Loc.Tr("Clean_StatusCloudOnly")
                    });
                }

                UpdateSummaryStatus();
            }
            catch
            {
            }
        }

        private void UpdateSummaryStatus()
        {
            var localOnlyItems = LocalItems.Where(i => !i.IsCloudOnly).ToList();
            int cloudOnlyCount = LocalItems.Count(i => i.IsCloudOnly);
            long totalBytes = localOnlyItems.Sum(i => i.SizeBytes);

            if (localOnlyItems.Count == 0 && cloudOnlyCount == 0)
            {
                TxtStatus.Text = Loc.Format("Clean_NoLocalsFound", _cacheRoot);
                TxtStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
                BtnDeleteSelected.IsEnabled = false;
            }
            else if (cloudOnlyCount > 0)
            {
                TxtStatus.Text = Loc.Format("Clean_FoundSummaryWithCloud", localOnlyItems.Count, _cacheRoot, FormatSize(totalBytes), cloudOnlyCount);
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 132, 199));
                BtnDeleteSelected.IsEnabled = LocalItems.Any(i => i.CanSelect);
            }
            else
            {
                TxtStatus.Text = Loc.Format("Clean_FoundSummary", localOnlyItems.Count, _cacheRoot, FormatSize(totalBytes));
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 132, 199));
                BtnDeleteSelected.IsEnabled = LocalItems.Any(i => i.CanSelect);
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in LocalItems)
            {
                if (item.CanSelect) item.IsSelected = true;
            }
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in LocalItems)
            {
                item.IsSelected = false;
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            ScanLocalCopies();
            await AppendCloudOnlyProjectsAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = LocalItems.Where(i => i.IsSelected && i.CanSelect).ToList();
            if (selected.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    Loc.Tr("Clean_NothingSelected"),
                    Loc.Tr("Clean_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            bool alsoCleanCentral = ChkAlsoCleanCentral.IsChecked == true;
            var projectsToCleanCentral = selected
                .Where(i => alsoCleanCentral || i.IsCentralCacheOnly)
                .Select(i => i.ProjectDir)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            long selectedBytes = selected.Sum(i => i.SizeBytes);
            string confirmMsg = Loc.Format("Clean_ConfirmBody", selected.Count, FormatSize(selectedBytes));
            if (alsoCleanCentral && projectsToCleanCentral.Count > 0)
            {
                confirmMsg += Loc.Format("Clean_ConfirmWithCentralNote", projectsToCleanCentral.Count);
            }

            var confirm = System.Windows.MessageBox.Show(
                confirmMsg,
                Loc.Tr("Clean_ConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            int deletedCount = 0;
            long freedBytes = 0;
            var errors = new List<string>();

            foreach (var item in selected.Where(i => !i.IsCentralCacheOnly))
            {
                bool itemOk = true;
                if (!string.IsNullOrEmpty(item.RvtFilePath) && File.Exists(item.RvtFilePath))
                {
                    if (!TryDeleteFile(item.RvtFilePath, out string? err))
                    {
                        itemOk = false;
                        errors.Add($"{item.DisplayName}: {err}");
                    }
                }

                if (!string.IsNullOrEmpty(item.BackupDirPath) && Directory.Exists(item.BackupDirPath))
                {
                    if (!TryDeleteDirectory(item.BackupDirPath, out string? err))
                    {
                        itemOk = false;
                        errors.Add($"{Path.GetFileName(item.BackupDirPath)}: {err}");
                    }
                }

                if (itemOk)
                {
                    deletedCount++;
                    freedBytes += item.SizeBytes;
                }
            }

            foreach (var projDir in projectsToCleanCentral)
            {
                string projName = Path.GetFileName(projDir);
                bool wasCentralOnlyRow = selected.Any(i => i.IsCentralCacheOnly && string.Equals(i.ProjectDir, projDir, StringComparison.OrdinalIgnoreCase));

                if (IsProjectOpenInRevit(projDir))
                {
                    errors.Add($"{projName} (Central): {Loc.Tr("Clean_StatusOpenInRevit")}");
                    continue;
                }

                bool centralOk = true;
                long centralFreed = 0;

                string centralDir = Path.Combine(projDir, "Central");
                if (Directory.Exists(centralDir))
                {
                    long cSize = ComputeDirectorySize(centralDir);
                    if (TryDeleteDirectory(centralDir, out string? err))
                    {
                        centralFreed += cSize;
                    }
                    else
                    {
                        centralOk = false;
                        errors.Add($"{projName}\\Central: {err}");
                    }
                }

                string manifestPath = Path.Combine(projDir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    TryDeleteFile(manifestPath, out _);
                }

                string connectorDir = Path.Combine(projDir, ".connector");
                if (Directory.Exists(connectorDir))
                {
                    TryDeleteDirectory(connectorDir, out _);
                }

                if (wasCentralOnlyRow)
                {
                    string localDir = Path.Combine(projDir, "Local");
                    if (Directory.Exists(localDir))
                    {
                        TryDeleteDirectory(localDir, out _);
                    }
                    string linksDir = Path.Combine(projDir, "Links");
                    if (Directory.Exists(linksDir))
                    {
                        centralFreed += ComputeDirectorySize(linksDir);
                        TryDeleteDirectory(linksDir, out _);
                    }
                    if (centralOk)
                    {
                        deletedCount++;
                    }
                }

                freedBytes += centralFreed;

                // Se la cartella progetto in cache è rimasta vuota (o contiene solo una cartella Local vuota), rimuovila
                try
                {
                    string localDir = Path.Combine(projDir, "Local");
                    if (Directory.Exists(localDir) && !Directory.EnumerateFileSystemEntries(localDir).Any())
                    {
                        Directory.Delete(localDir, false);
                    }
                    if (Directory.Exists(projDir) && !Directory.EnumerateFileSystemEntries(projDir).Any())
                    {
                        Directory.Delete(projDir, false);
                    }
                }
                catch { }
            }

            ScanLocalCopies();
            await AppendCloudOnlyProjectsAsync();

            if (errors.Count == 0)
            {
                TxtStatus.Text = Loc.Format("Clean_DoneSummary", deletedCount, FormatSize(freedBytes));
                TxtStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
            }
            else
            {
                string detail = string.Join("\n", errors.Take(8));
                TxtStatus.Text = Loc.Format("Clean_DoneSummary", deletedCount, FormatSize(freedBytes));
                TxtStatus.Foreground = System.Windows.Media.Brushes.DarkOrange;
                System.Windows.MessageBox.Show(
                    Loc.Format("Clean_DoneWithErrors", errors.Count, deletedCount, FormatSize(freedBytes), detail),
                    Loc.Tr("Clean_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private static bool TryDeleteFile(string path, out string? error)
        {
            error = null;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryDeleteDirectory(string dirPath, out string? error)
        {
            error = null;
            try
            {
                var di = new DirectoryInfo(dirPath);
                foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))
                {
                    try { fi.Attributes = FileAttributes.Normal; } catch { }
                }
                di.Delete(true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static long ComputeDirectorySize(string dirPath)
        {
            long sum = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try { sum += new FileInfo(file).Length; } catch { }
                }
            }
            catch { }
            return sum;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1024L * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }
    }
}
