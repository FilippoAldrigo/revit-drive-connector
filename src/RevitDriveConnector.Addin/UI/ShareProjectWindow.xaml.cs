using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
 public partial class ShareProjectWindow : Window
 {
 private readonly string _projectName;
 private readonly NamedPipeIpcClient _ipcClient = new();
 public ObservableCollection<CollaboratorItem> Collaborators { get; } = new();

 public ShareProjectWindow(string projectName)
 {
 InitializeComponent();
 IconHelper.ApplyWindowIcon(this);
 _projectName = projectName;
 TxtProjectName.Text = projectName;
 LvCollaborators.ItemsSource = Collaborators;

 ApplyLocalization();

 Loaded += async (s, e) => await RefreshCollaboratorsAsync();
 }

 private void ApplyLocalization()
 {
 Title = Loc.Tr("Share_Title");
 TxtHeaderTitle.Text = Loc.Tr("Share_Header");
 TxtHeaderSubtitle.Text = Loc.Tr("Share_Subtitle");
 LblSelectedProject.Text = Loc.Tr("Share_SelectedProject");
 TxtAddHeader.Text = Loc.Tr("Share_AddHeader");
 TxtEmail.Text = Loc.Tr("Share_EmailPlaceholder");
 CmbItemEditor.Content = Loc.Tr("Share_RoleEditor");
 CmbItemViewer.Content = Loc.Tr("Share_RoleViewer");
 BtnAdd.Content = Loc.Tr("Share_BtnAdd");
 ChkNotify.Content = Loc.Tr("Share_NotifyCheck");
 TxtActiveMembers.Text = Loc.Tr("Share_ActiveMembers");

 ColEmail.Header = Loc.Tr("Share_ColEmail");
 ColName.Header = Loc.Tr("Share_ColName");
 ColRole.Header = Loc.Tr("Share_ColRole");
 ColAction.Header = Loc.Tr("Share_ColAction");

 BtnRefresh.Content = Loc.Tr("Share_BtnRefresh");
 BtnClose.Content = Loc.Tr("Share_BtnClose");
 }

 private void TxtEmail_GotFocus(object sender, RoutedEventArgs e)
 {
 if (TxtEmail.Text == "collega@gmail.com" || TxtEmail.Text == "teammate@gmail.com")
 {
 TxtEmail.Text = "";
 TxtEmail.Foreground = Brushes.Black;
 }
 }

 private async void TxtEmail_KeyDown(object sender, KeyEventArgs e)
 {
 if (e.Key == Key.Enter)
 {
 await AddCollaboratorAsync();
 }
 }

 private async void BtnAdd_Click(object sender, RoutedEventArgs e)
 {
 await AddCollaboratorAsync();
 }

 private async Task AddCollaboratorAsync()
 {
 string email = TxtEmail.Text.Trim();
 if (string.IsNullOrWhiteSpace(email) || !email.Contains("@") || email == "collega@gmail.com" || email == "teammate@gmail.com")
 {
 TxtFeedback.Text = Loc.Tr("Share_InvalidEmail");
 TxtFeedback.Foreground = Brushes.Crimson;
 return;
 }

 string role = CmbRole.SelectedIndex == 1 ? "reader" : "writer";
 bool notify = ChkNotify.IsChecked == true;

 BtnAdd.IsEnabled = false;
 TxtFeedback.Text = Loc.Tr("Share_Sending");
 TxtFeedback.Foreground = Brushes.DarkOrange;

 try
 {
 var req = new AddCollaboratorRequest
 {
 ProjectName = _projectName,
 Email = email,
 Role = role,
 SendNotificationEmail = notify
 };

 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.AddCollaborator,
 PayloadJson = JsonSerializer.Serialize(req)
 }, timeoutMs: 15000);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var result = JsonSerializer.Deserialize<CollaboratorOperationResult>(resp.DataJson);
 if (result != null && result.Success)
 {
 TxtFeedback.Text = Loc.Format("Share_Success", email);
 TxtFeedback.Foreground = Brushes.Green;
 TxtEmail.Text = "";
 await RefreshCollaboratorsAsync();
 }
 else
 {
 TxtFeedback.Text = result?.Message ?? Loc.Tr("Common_Error");
 TxtFeedback.Foreground = Brushes.Crimson;
 }
 }
 else
 {
 TxtFeedback.Text = resp.ErrorMessage ?? Loc.Tr("Common_Error");
 TxtFeedback.Foreground = Brushes.Crimson;
 }
 }
 catch (Exception ex)
 {
 TxtFeedback.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
 TxtFeedback.Foreground = Brushes.Crimson;
 }
 finally
 {
 BtnAdd.IsEnabled = true;
 }
 }

 private async Task RefreshCollaboratorsAsync()
 {
 try
 {
 var reqPayload = JsonSerializer.Serialize(new Dictionary<string, string>
 {
 ["ProjectName"] = _projectName
 });

 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.ListCollaborators,
 PayloadJson = reqPayload
 }, timeoutMs: 10000);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var list = JsonSerializer.Deserialize<List<CollaboratorItem>>(resp.DataJson);
 Collaborators.Clear();
 if (list != null)
 {
 foreach (var item in list)
 {
 Collaborators.Add(item);
 }
 }
 }
 }
 catch (Exception ex)
 {
 Console.WriteLine($"[ShareProjectWindow] Error refreshing list: {ex.Message}");
 }
 }

 private async void BtnRemove_Click(object sender, RoutedEventArgs e)
 {
 if (sender is Button btn && btn.DataContext is CollaboratorItem item)
 {
 var mbr = MessageBox.Show(
 Loc.Format("Share_ConfirmRevoke", item.Email),
 Loc.Tr("Share_ConfirmRevokeTitle"),
 MessageBoxButton.YesNo,
 MessageBoxImage.Question);

 if (mbr != MessageBoxResult.Yes) return;

 try
 {
 var req = new RemoveCollaboratorRequest
 {
 ProjectName = _projectName,
 PermissionId = item.Id
 };

 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.RemoveCollaborator,
 PayloadJson = JsonSerializer.Serialize(req)
 }, timeoutMs: 15000);

 if (resp.Success)
 {
 TxtFeedback.Text = Loc.Format("Share_Revoked", item.Email);
 TxtFeedback.Foreground = Brushes.Green;
 await RefreshCollaboratorsAsync();
 }
 else
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}:\n{resp.ErrorMessage}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }
 catch (Exception ex)
 {
 MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
 }
 }
 }

 private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
 {
 await RefreshCollaboratorsAsync();
 }

 private void BtnClose_Click(object sender, RoutedEventArgs e)
 {
 Close();
 }
 }
}
