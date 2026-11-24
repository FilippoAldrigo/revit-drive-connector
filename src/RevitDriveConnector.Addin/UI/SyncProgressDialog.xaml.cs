using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
    /// <summary>
    /// Modal WPF progress window keeping the Revit UI message pump responsive during large (up to 1 GB+)
    /// chunked resumable uploads/downloads, displaying real-time MB/s, file counts, and safe cancellation.
    /// </summary>
    public partial class SyncProgressDialog : Window
    {
        private readonly NamedPipeIpcClient _ipcClient = new();
        private readonly IpcRequest _request;
        private readonly int _timeoutMs;
        private CancellationTokenSource? _cts;

        public IpcResponse? Response { get; private set; }
        public bool UserCancelled { get; private set; }

        public SyncProgressDialog(string title, string subtitle, IpcRequest request, int timeoutMs = 1800000)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            Title = title;
            TxtTitle.Text = title;
            TxtSubtitle.Text = subtitle;
            _request = request;
            _timeoutMs = timeoutMs;

            Loaded += async (s, e) => await ExecuteTransferAsync();
        }

        private async Task ExecuteTransferAsync()
        {
            _cts = new CancellationTokenSource();
            try
            {
                Response = await Task.Run(() => _ipcClient.SendRequestAsync(
                    _request,
                    timeoutMs: _timeoutMs,
                    ct: _cts.Token,
                    progressCallback: OnProgressUpdate));

                DialogResult = Response.Success;
                Close();
            }
            catch (OperationCanceledException)
            {
                UserCancelled = true;
                Response = new IpcResponse
                {
                    RequestId = _request.RequestId,
                    Success = false,
                    ErrorMessage = "Operazione annullata dall'utente."
                };
                DialogResult = false;
                Close();
            }
            catch (Exception ex)
            {
                Response = new IpcResponse
                {
                    RequestId = _request.RequestId,
                    Success = false,
                    ErrorMessage = ex.Message
                };
                DialogResult = false;
                Close();
            }
        }

        private void OnProgressUpdate(TransferProgressInfo info)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!string.IsNullOrWhiteSpace(info.Phase))
                    {
                        TxtSubtitle.Text = info.Phase;
                    }

                    if (info.TotalBytes > 0)
                    {
                        PrgTransfer.IsIndeterminate = false;
                        PrgTransfer.Value = Math.Min(100.0, Math.Max(0.0, info.Percentage));

                        double mbDone = info.BytesTransferred / (1024.0 * 1024.0);
                        double mbTotal = info.TotalBytes / (1024.0 * 1024.0);
                        double mbPerSec = info.BytesPerSecond / (1024.0 * 1024.0);
                        string speedStr = mbPerSec >= 0.05 ? $" • {mbPerSec:F1} MB/s" : "";
                        TxtStats.Text = $"{info.Phase}: {info.CompletedFiles}/{info.TotalFiles} – {mbDone:F1} MB / {mbTotal:F1} MB{speedStr} ({info.Percentage:F0}%)";
                    }
                    else if (info.TotalFiles > 0)
                    {
                        PrgTransfer.IsIndeterminate = false;
                        PrgTransfer.Value = info.Percentage;
                        TxtStats.Text = $"{info.Phase}: File {info.CompletedFiles}/{info.TotalFiles}";
                    }
                    else
                    {
                        PrgTransfer.IsIndeterminate = true;
                        TxtStats.Text = "Elaborazione in corso...";
                    }

                    if (!string.IsNullOrWhiteSpace(info.CurrentFileName))
                    {
                        TxtFile.Text = $"File: {info.CurrentFileName}";
                    }
                }));
            }
            catch { }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            UserCancelled = true;
            BtnCancel.IsEnabled = false;
            TxtStats.Text = "Annullamento in corso...";
            try { _cts?.Cancel(); } catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            base.OnClosed(e);
        }
    }
}
