using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
 public partial class SettingsWindow : Window
 {
 private readonly AppConfig _config;
 private readonly NamedPipeIpcClient _ipcClient = new();

 public SettingsWindow()
 {
 InitializeComponent();
 IconHelper.ApplyWindowIcon(this);
 _config = AppConfig.Load();

 ApplyLocalization();

 TxtCloudPath.Text = _config.CloudStoragePath;
 TxtCachePath.Text = _config.LocalCachePath;
 TxtCoordinatorUrl.Text = _config.CoordinatorUrl;

 CmbLanguage.SelectedIndex = _config.Language switch
 {
 "it" => 1,
 "en" => 2,
 _ => 0
 };
 ChkEnableLiveLocks.IsChecked = _config.EnableLiveElementLocks;

 Loaded += async (s, e) => await LoadLiveStatusAsync();
 }

 private async Task LoadLiveStatusAsync()
 {
 try
 {
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.GetDaemonStatus
 }, timeoutMs: 5000);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var info = JsonSerializer.Deserialize<DaemonStatusInfo>(resp.DataJson);
 if (info != null)
 {
 TxtStatus.Text = info.GoogleDriveConnected
 ? $"Stato: Connesso a Google Drive ({info.GoogleAccountEmail})"
 : $"Stato: Account Google non connesso (Modalità cartella locale: {info.CloudStoragePath})";
 return;
 }
 }
 }
 catch { }
 TxtStatus.Text = "Stato: Motore cloud locale pronto.";
 }

 private void ApplyLocalization()
 {
 Title = Loc.Tr("Settings_Title");
 TxtHeaderTitle.Text = Loc.Tr("Settings_Header");
 TxtHeaderSubtitle.Text = Loc.Tr("Settings_Subtitle");
 LblCloudPath.Text = Loc.Tr("Settings_CloudPath");
 LblCachePath.Text = Loc.Tr("Settings_CachePath");
 LblCoordUrl.Text = Loc.Tr("Settings_CoordUrl");
 LblLanguage.Text = Loc.Tr("Settings_Language");
 ChkEnableLiveLocks.Content = Loc.Tr("Settings_EnableLiveLocks");
 BtnBrowseCloud.Content = Loc.Tr("Settings_BtnBrowse");
 BtnBrowseCache.Content = Loc.Tr("Settings_BtnBrowse");
 BtnCancel.Content = Loc.Tr("Common_Cancel");
 BtnSave.Content = Loc.Tr("Settings_BtnSave");
 }

 private void BtnBrowseCloud_Click(object sender, RoutedEventArgs e)
 {
 using var dlg = new System.Windows.Forms.FolderBrowserDialog
 {
 Description = Loc.Tr("Settings_CloudPath"),
 SelectedPath = Directory.Exists(TxtCloudPath.Text) ? TxtCloudPath.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
 };

 if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
 {
 TxtCloudPath.Text = dlg.SelectedPath;
 }
 }

 private void BtnBrowseCache_Click(object sender, RoutedEventArgs e)
 {
 using var dlg = new System.Windows.Forms.FolderBrowserDialog
 {
 Description = Loc.Tr("Settings_CachePath"),
 SelectedPath = Directory.Exists(TxtCachePath.Text) ? TxtCachePath.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
 };

 if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
 {
 TxtCachePath.Text = dlg.SelectedPath;
 }
 }

 private void BtnCancel_Click(object sender, RoutedEventArgs e)
 {
 DialogResult = false;
 Close();
 }

 private void BtnSave_Click(object sender, RoutedEventArgs e)
 {
 if (string.IsNullOrWhiteSpace(TxtCloudPath.Text))
 {
 MessageBox.Show("Il percorso dello storage cloud non puo essere vuoto.", "Attenzione", MessageBoxButton.OK, MessageBoxImage.Warning);
 return;
 }
 if (string.IsNullOrWhiteSpace(TxtCachePath.Text))
 {
 MessageBox.Show("Il percorso della cache locale non puo essere vuoto.", "Attenzione", MessageBoxButton.OK, MessageBoxImage.Warning);
 return;
 }

 _config.CloudStoragePath = TxtCloudPath.Text.Trim();
 _config.LocalCachePath = TxtCachePath.Text.Trim();
 _config.CoordinatorUrl = TxtCoordinatorUrl.Text.Trim();
 _config.Language = CmbLanguage.SelectedIndex switch
 {
 1 => "it",
 2 => "en",
 _ => "Auto"
 };
 _config.EnableLiveElementLocks = ChkEnableLiveLocks.IsChecked == true;

 Loc.Initialize(_config.Language);
 _config.Save();
 Services.LiveElementLockService.ReloadSettings();

 try
 {
 Directory.CreateDirectory(_config.CloudStoragePath);
 Directory.CreateDirectory(_config.LocalCachePath);
 }
 catch { }

 MessageBox.Show(Loc.Tr("Settings_SavedMsg"), Loc.Tr("Settings_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
 DialogResult = true;
 Close();
 }
 }
}
