using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

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

            TxtCachePath.Text = _config.LocalCachePath;
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
                            ? $"Stato: Connesso a Google Drive API ({info.GoogleAccountEmail})"
                            : "Stato: Account Google non connesso. Apri 'Account Google' per accedere.";
                        return;
                    }
                }
            }
            catch { }
            TxtStatus.Text = "Stato: Motore cloud serverless pronto.";
        }

        private void ApplyLocalization()
        {
            Title = Loc.Tr("Settings_Title");
            TxtHeaderTitle.Text = Loc.Tr("Settings_Header");
            LblCachePath.Text = Loc.Tr("Settings_CachePath");
            LblLanguage.Text = Loc.Tr("Settings_Language");
            ChkEnableLiveLocks.Content = Loc.Tr("Settings_EnableLiveLocks");
            BtnBrowseCache.Content = Loc.Tr("Settings_BtnBrowse");
            BtnCancel.Content = Loc.Tr("Common_Cancel");
            BtnSave.Content = Loc.Tr("Settings_BtnSave");
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

        private async void BtnRunCleanup_Click(object sender, RoutedEventArgs e)
        {
            BtnRunCleanup.IsEnabled = false;
            try
            {
                var resp = await Task.Run(() => _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.RunDiskCleanup
                }, timeoutMs: 15000));

                if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                {
                    var cleanRes = JsonSerializer.Deserialize<DiskCleanupResult>(resp.DataJson);
                    MessageBox.Show(cleanRes?.Message ?? "Pulizia eseguita.", "Pulizia Cache & Temp", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Pulizia non riuscita: {resp.ErrorMessage}", "Pulizia Cache & Temp", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore pulizia: {ex.Message}", "Pulizia Cache & Temp", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRunCleanup.IsEnabled = true;
            }
        }

        private async void BtnExportDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Archivio Zip Diagnostica (*.zip)|*.zip",
                FileName = $"RevitDriveConnector_Diagnostics_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                Title = "Salva Archivio Diagnostica & Log"
            };

            if (sfd.ShowDialog() != true) return;

            BtnExportDiagnostics.IsEnabled = false;
            try
            {
                var resp = await Task.Run(() => _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.ExportDiagnostics,
                    PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["DestinationZipPath"] = sfd.FileName
                    })
                }, timeoutMs: 30000));

                if (resp.Success && !string.IsNullOrEmpty(resp.DataJson))
                {
                    var expRes = JsonSerializer.Deserialize<DiagnosticsExportResponse>(resp.DataJson);
                    double mb = (expRes?.ZipSizeBytes ?? 0) / (1024.0 * 1024.0);
                    var askOpen = MessageBox.Show(
                        $"Diagnostica esportata con successo!\n\n" +
                        $"• Percorso: {sfd.FileName}\n" +
                        $"• File inclusi: {expRes?.FileCount ?? 0} (log, config anonimizzato, manifest)\n" +
                        $"• Dimensione: {mb:F2} MB\n\n" +
                        "Vuoi aprire la cartella del file generato?",
                        "Export Diagnostica Completato",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (askOpen == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{sfd.FileName}\"",
                            UseShellExecute = true
                        });
                    }
                }
                else
                {
                    MessageBox.Show($"Esportazione non riuscita: {resp.ErrorMessage}", "Errore Diagnostica", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore esportazione: {ex.Message}", "Errore Diagnostica", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExportDiagnostics.IsEnabled = true;
            }
        }

        private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppLogger.LogDirectory,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Impossibile aprire la cartella log:\n{ex.Message}", "Log", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtCachePath.Text))
            {
                MessageBox.Show("Il percorso della cache locale non può essere vuoto.", "Attenzione", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _config.LocalCachePath = TxtCachePath.Text.Trim();
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
                Directory.CreateDirectory(_config.LocalCachePath);
            }
            catch { }

            MessageBox.Show(Loc.Tr("Settings_SavedMsg"), Loc.Tr("Settings_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }
    }
}
