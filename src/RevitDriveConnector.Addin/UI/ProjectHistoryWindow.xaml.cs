using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;
using RevitDriveConnector.Core.Utils;

namespace RevitDriveConnector.Addin.UI
{
    public class RevisionDisplayRow
    {
        public string RevisionId { get; set; } = string.Empty;
        public long Version { get; set; }
        public DateTime ModifiedTimeUtc { get; set; }
        public long SizeBytes { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public partial class ProjectHistoryWindow : Window
    {
        private readonly NamedPipeIpcClient _ipcClient = new();
        private readonly string _projectId;
        private readonly string _currentUser;

        public ProjectHistoryWindow(string projectId, string currentUser)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _projectId = projectId;
            _currentUser = currentUser;

            TxtTitle.Text = $"Cronologia Modello Centrale: '{projectId}'";
            Loaded += async (s, e) => await LoadHistoryAsync();
        }

        private async Task LoadHistoryAsync()
        {
            TxtSubtitle.Text = "Interrogazione revisioni Google Drive in corso...";
            BtnRollback.IsEnabled = false;

            try
            {
                var resp = await Task.Run(() => _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.GetVersionHistory,
                    PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["ProjectId"] = _projectId })
                }, timeoutMs: 20000));

                if (!resp.Success || string.IsNullOrEmpty(resp.DataJson))
                {
                    TxtSubtitle.Text = $"Impossibile recuperare la cronologia: {resp.ErrorMessage}";
                    return;
                }

                var history = JsonSerializer.Deserialize<ProjectHistoryResponse>(resp.DataJson);
                if (history == null || history.Revisions == null || history.Revisions.Count == 0)
                {
                    TxtSubtitle.Text = "Nessuna revisione storica trovata per questo progetto.";
                    LstRevisions.ItemsSource = null;
                    return;
                }

                var rows = new List<RevisionDisplayRow>();
                foreach (var r in history.Revisions)
                {
                    double mb = r.SizeBytes / (1024.0 * 1024.0);
                    rows.Add(new RevisionDisplayRow
                    {
                        RevisionId = r.RevisionId,
                        Version = r.Version,
                        ModifiedTimeUtc = r.ModifiedTimeUtc.ToLocalTime(),
                        SizeBytes = r.SizeBytes,
                        AuthorName = string.IsNullOrWhiteSpace(r.AuthorName) ? r.AuthorEmail : r.AuthorName,
                        Description = $"{r.Description} ({mb:F1} MB)"
                    });
                }

                LstRevisions.ItemsSource = rows;
                TxtSubtitle.Text = $"Versione centrale attiva: v{history.CurrentVersion} (disponibili {rows.Count} revisioni).";
            }
            catch (Exception ex)
            {
                TxtSubtitle.Text = $"Errore di connessione: {ex.Message}";
                AppLogger.Error("ProjectHistoryWindow", "Errore caricamento cronologia", ex);
            }
        }

        private void LstRevisions_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            BtnRollback.IsEnabled = LstRevisions.SelectedItem is RevisionDisplayRow;
        }

        private async void BtnRollback_Click(object sender, RoutedEventArgs e)
        {
            if (LstRevisions.SelectedItem is not RevisionDisplayRow selected) return;

            var confirm = MessageBox.Show(
                $"ATTENZIONE: Stai per ripristinare il modello centrale alla versione del {selected.ModifiedTimeUtc:yyyy-MM-dd HH:mm:ss} (Autore: {selected.AuthorName}).\n\n" +
                "Questa operazione creerà una nuova versione centrale su Google Drive basata su questa revisione passata.\n" +
                "Tutti i collaboratori del team dovranno sincronizzare o riaprire il modello per allinearsi.\n\n" +
                "Vuoi procedere con il Rollback?",
                "Conferma Rollback Modello Centrale",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            var rollbackPayload = JsonSerializer.Serialize(new RollbackRequest
            {
                ProjectId = _projectId,
                RevisionId = selected.RevisionId,
                User = _currentUser,
                Machine = Environment.MachineName,
                Reason = $"Rollback manuale a revisione {selected.Version} ({selected.ModifiedTimeUtc:yyyy-MM-dd HH:mm})"
            });

            var progressDlg = new SyncProgressDialog(
                $"Rollback Progetto '{_projectId}'",
                "Scaricamento e ripristino revisione storica...",
                new IpcRequest
                {
                    Command = IpcCommand.RollbackProjectVersion,
                    PayloadJson = rollbackPayload
                },
                timeoutMs: 600000);

            progressDlg.ShowDialog();
            var resp = progressDlg.Response;

            if (resp != null && resp.Success)
            {
                MessageBox.Show(
                    $"Rollback eseguito con successo!\n\nÈ stata creata la nuova versione centrale su Google Drive.\n" +
                    "Per continuare a lavorare in sicurezza, chiudi e riapri il modello da 'Apri da Google Drive'.",
                    "Rollback Completato",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await LoadHistoryAsync();
            }
            else
            {
                MessageBox.Show(
                    $"Impossibile completare il rollback:\n{resp?.ErrorMessage ?? "Errore sconosciuto"}",
                    "Errore Rollback",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadHistoryAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
