using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
 public partial class SyncGatekeeperDialog : Window
 {
 private readonly NamedPipeIpcClient _ipcClient = new();
 private readonly string _projectId;
 private readonly string _username;
 private CancellationTokenSource? _cts;
 public bool IsAlignedAndReady { get; private set; }
 public bool UserCancelled { get; private set; }

 public SyncGatekeeperDialog(string projectId, string username)
 {
 InitializeComponent();
 IconHelper.ApplyWindowIcon(this);
 _projectId = projectId;
 _username = username;

 ApplyLocalization();

 Loaded += async (s, e) => await StartAlignmentWatchLoopAsync();
 }

 private void ApplyLocalization()
 {
 Title = Loc.Tr("Gatekeeper_Title");
 TxtHeaderTitle.Text = Loc.Tr("Gatekeeper_Header");
 TxtSubtitle.Text = Loc.Tr("Gatekeeper_Subtitle");
 TxtStatus.Text = Loc.Tr("Gatekeeper_Waiting");
 BtnCancel.Content = Loc.Tr("Common_Cancel");
 }

 private async Task StartAlignmentWatchLoopAsync()
 {
 _cts = new CancellationTokenSource();
 int elapsedSeconds = 0;
 const int maxWaitSeconds = 240;

 try
 {
 while (!_cts.Token.IsCancellationRequested && elapsedSeconds < maxWaitSeconds)
 {
 var payload = JsonSerializer.Serialize(new
 {
 ProjectId = _projectId,
 User = _username
 });

 var resp = await _ipcClient.SendRequestAsync(new IpcRequest
 {
 Command = IpcCommand.VerifySyncIntegrity,
 PayloadJson = payload
 }, timeoutMs: 180000, ct: _cts.Token);

 if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
 {
 var result = JsonSerializer.Deserialize<SyncIntegrityResult>(resp.DataJson);
 if (result != null)
 {
 if (result.IsAligned)
 {
 TxtStatus.Text = Loc.Tr("Gatekeeper_Aligned");
 TxtStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
 await Task.Delay(500);

 IsAlignedAndReady = true;
 DialogResult = true;
 Close();
 return;
 }

 if (!string.IsNullOrEmpty(result.Message))
 {
 TxtStatus.Text = result.Message;
 }
 }
 }

 await Task.Delay(1500, _cts.Token);
 elapsedSeconds += 2;
 TxtTimer.Text = Loc.Format("Gatekeeper_Timer", elapsedSeconds, maxWaitSeconds);
 }

 if (!IsAlignedAndReady)
 {
 TxtStatus.Text = Loc.Tr("Gatekeeper_Timeout");
 TxtStatus.Foreground = System.Windows.Media.Brushes.Coral;
 await Task.Delay(1500);
 DialogResult = false;
 Close();
 }
 }
 catch (OperationCanceledException)
 {
 DialogResult = false;
 Close();
 }
 catch (Exception ex)
 {
 TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
 await Task.Delay(1500);
 DialogResult = false;
 Close();
 }
 }

 private void BtnCancel_Click(object sender, RoutedEventArgs e)
 {
 UserCancelled = true;
 _cts?.Cancel();
 DialogResult = false;
 Close();
 }
 }
}
