using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
 public partial class GoogleAccountWindow : Window
 {
 private readonly NamedPipeIpcClient _ipcClient = new();

 public GoogleAccountWindow()
 {
 InitializeComponent();
 IconHelper.ApplyWindowIcon(this);
 ApplyLocalization();

 Loaded += async (s, e) => { await App.EnsureDaemonRunningAsync(); await RefreshStatusAsync(); };
 }

 private void ApplyLocalization()
 {
 Title = Loc.Tr("Account_Title");
 TxtHeaderTitle.Text = Loc.Tr("Account_Header");
 TxtHeaderSubtitle.Text = Loc.Tr("Account_Subtitle");

 TxtDisplayName.Text = Loc.Tr("Account_NoConnected");
 TxtEmail.Text = Loc.Tr("Account_Prompt");
 TxtStatus.Text = Loc.Tr("Account_NotConnected");

 BtnConnect.Content = Loc.Tr("Account_BtnConnect");
 BtnDisconnect.Content = Loc.Tr("Account_BtnDisconnect");

 TxtCredHeader.Text = Loc.Tr("Account_CredHeader");
 TxtCredDesc.Text = Loc.Tr("Account_CredDesc");
 BtnLoadCredentials.Content = Loc.Tr("Account_BtnLoadCred");
 ExpManual.Header = Loc.Tr("Account_ManualHeader");
 BtnSaveManual.Content = Loc.Tr("Account_BtnSaveManual");

 TxtGuideTitle.Text = Loc.Tr("Account_GuideTitle");
 TxtGuide1.Text = Loc.Tr("Account_Guide1");
 TxtGuide2.Text = Loc.Tr("Account_Guide2");
 TxtGuide3.Text = Loc.Tr("Account_Guide3");

 BtnRefresh.Content = Loc.Tr("Share_BtnRefresh");
 BtnClose.Content = Loc.Tr("Common_Close");
 }

 private async Task RefreshStatusAsync()
 {
 TxtStatus.Text = Loc.Tr("Account_CheckingStatus");
 TxtStatus.Foreground = System.Windows.Media.Brushes.Gray;

 try
 {
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.GetGoogleAccountStatus
 }, timeoutMs: 10000);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var acct = JsonSerializer.Deserialize<GoogleAccountInfo>(resp.DataJson);
 UpdateUi(acct);
 }
 else
 {
 TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {resp.ErrorMessage ?? "Daemon not responding"}";
 TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
 }
 }
 catch (Exception ex)
 {
 TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
 TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
 }
 }

 private void UpdateUi(GoogleAccountInfo? acct)
 {
 if (acct == null) return;

 if (acct.IsConnected)
 {
 TxtDisplayName.Text = string.IsNullOrEmpty(acct.DisplayName) ? acct.Email : acct.DisplayName;
 TxtEmail.Text = acct.Email;
 TxtStatus.Text = Loc.Tr("Account_Connected");
 TxtStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;

 BtnConnect.Visibility = Visibility.Collapsed;
 BtnDisconnect.Visibility = Visibility.Visible;
 }
 else
 {
 TxtDisplayName.Text = Loc.Tr("Account_NoConnected");
 TxtEmail.Text = Loc.Tr("Account_Prompt");
 TxtStatus.Text = string.IsNullOrEmpty(acct.StatusMessage) ? Loc.Tr("Account_NotConnected") : acct.StatusMessage;
 TxtStatus.Foreground = System.Windows.Media.Brushes.Crimson;

 BtnConnect.Visibility = Visibility.Visible;
 BtnDisconnect.Visibility = Visibility.Collapsed;
 BtnConnect.IsEnabled = acct.HasClientCredentials;
 }

 if (acct.HasClientCredentials)
 {
 TxtCredStatus.Text = Loc.Tr("Account_CredConfigured");
 TxtCredStatus.Foreground = System.Windows.Media.Brushes.ForestGreen;
 }
 else
 {
 TxtCredStatus.Text = Loc.Tr("Account_CredMissing");
 TxtCredStatus.Foreground = System.Windows.Media.Brushes.Crimson;
 }
 }

 private async void BtnConnect_Click(object sender, RoutedEventArgs e)
 {
 BtnConnect.IsEnabled = false;
 TxtStatus.Text = "Opening browser for Google authorization...";
 TxtStatus.Foreground = System.Windows.Media.Brushes.DodgerBlue;

 try
 {
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.ConnectGoogleAccount
 }, timeoutMs: 120000);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var acct = JsonSerializer.Deserialize<GoogleAccountInfo>(resp.DataJson);
 UpdateUi(acct);
 MessageBox.Show($"Connected as: {acct?.Email}", 
 Loc.Tr("Account_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
 }
 else
 {
 MessageBox.Show($"Sign-in failed:\n{resp.ErrorMessage}", 
 Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
 await RefreshStatusAsync();
 }
 }
 catch (Exception ex)
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", 
 Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 await RefreshStatusAsync();
 }
 finally
 {
 BtnConnect.IsEnabled = true;
 }
 }

 private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
 {
 var result = MessageBox.Show(
 Loc.Tr("Account_ConfirmDisconnect"),
 Loc.Tr("Account_BtnDisconnect"), MessageBoxButton.YesNo, MessageBoxImage.Question);

 if (result != MessageBoxResult.Yes) return;

 try
 {
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.DisconnectGoogleAccount
 });

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var acct = JsonSerializer.Deserialize<GoogleAccountInfo>(resp.DataJson);
 UpdateUi(acct);
 MessageBox.Show(Loc.Tr("Account_DisconnectSuccess"), 
 Loc.Tr("Account_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
 }
 }
 catch (Exception ex)
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", 
 Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }

 private async void BtnLoadCredentials_Click(object sender, RoutedEventArgs e)
 {
 var ofd = new OpenFileDialog
 {
 Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
 Title = Loc.Tr("Account_BtnLoadCred")
 };

 if (ofd.ShowDialog() == true)
 {
 try
 {
 string json = await File.ReadAllTextAsync(ofd.FileName);
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.SaveGoogleCredentials,
 PayloadJson = json
 });

 if (resp.Success)
 {
 MessageBox.Show(Loc.Tr("Common_Success"), 
 Loc.Tr("Account_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
 await RefreshStatusAsync();
 }
 else
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {resp.ErrorMessage}", 
 Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }
 catch (Exception ex)
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }
 }

 private async void BtnSaveManualCredentials_Click(object sender, RoutedEventArgs e)
 {
 string cid = TxtClientId.Text.Trim();
 string csec = TxtClientSecret.Password.Trim();

 if (string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(csec))
 {
 MessageBox.Show("Please enter both Client ID and Client Secret.", 
 Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
 return;
 }

 var credObj = new
 {
 installed = new
 {
 client_id = cid,
 client_secret = csec,
 auth_uri = "https://accounts.google.com/o/oauth2/auth",
 token_uri = "https://oauth2.googleapis.com/token",
 redirect_uris = new[] { "http://localhost" }
 }
 };

 string json = JsonSerializer.Serialize(credObj, new JsonSerializerOptions { WriteIndented = true });

 try
 {
 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.SaveGoogleCredentials,
 PayloadJson = json
 });

 if (resp.Success)
 {
 MessageBox.Show(Loc.Tr("Common_Success"), 
 Loc.Tr("Account_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
 TxtClientId.Clear();
 TxtClientSecret.Clear();
 await RefreshStatusAsync();
 }
 else
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {resp.ErrorMessage}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }
 catch (Exception ex)
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }

 private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
 {
 await RefreshStatusAsync();
 }

 private void BtnClose_Click(object sender, RoutedEventArgs e)
 {
 Close();
 }
 }
}
