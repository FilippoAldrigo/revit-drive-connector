using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;
using RevitDriveConnector.Core.Models;

namespace RevitDriveConnector.Addin.UI
{
    public partial class BorrowToastWindow : Window
    {
        private static readonly NamedPipeIpcClient IpcClient = new();
        public BorrowRequest Request { get; }
        public string CurrentUser { get; }
        private readonly DispatcherTimer _autoDismissTimer;

        public event EventHandler? ToastClosed;

        public BorrowToastWindow(BorrowRequest request, string currentUser, int stackIndex = 0, bool isInformational = false)
        {
            InitializeComponent();
            Request = request;
            CurrentUser = currentUser;

            Title = Loc.Tr("Toast_Title");
            LblElement.Text = Loc.Tr("Toast_Element");
            LblUniqueId.Text = Loc.Tr("Toast_UniqueId");
            LblMessage.Text = Loc.Tr("Toast_Message");
            BtnGrant.Content = Loc.Tr("Toast_BtnGrant");
            BtnDeny.Content = Loc.Tr("Toast_BtnDeny");
            BtnOk.Content = Loc.Tr("Toast_BtnClose");

            if (isInformational)
            {
                TxtTitle.Text = request.Status == BorrowRequestStatus.Granted
                    ? Loc.Tr("Toast_TitleGranted")
                    : Loc.Tr("Toast_TitleDenied");

                TxtPrompt.Text = request.Status == BorrowRequestStatus.Granted
                    ? Loc.Format("Toast_PromptGranted", request.OwnerUser)
                    : Loc.Format("Toast_PromptDenied", request.OwnerUser);

                PanelButtons.Visibility = Visibility.Collapsed;
                BtnOk.Visibility = Visibility.Visible;
            }
            else
            {
                TxtTitle.Text = Loc.Tr("Toast_Title");
                TxtPrompt.Text = Loc.Format("Toast_Prompt", request.RequesterUser);
            }

            TxtElementName.Text = string.IsNullOrWhiteSpace(request.ElementName) ? "Revit Element" : request.ElementName;
            TxtElementId.Text = request.ElementId.ToString();

            if (!string.IsNullOrWhiteSpace(request.Message))
            {
                TxtMessage.Text = request.Message;
                PanelMessage.Visibility = Visibility.Visible;
            }

            Loaded += (s, e) =>
            {
                var workArea = SystemParameters.WorkArea;
                double width = ActualWidth > 0 ? ActualWidth : 400;
                double height = ActualHeight > 0 ? ActualHeight : 180;

                Left = workArea.Right - width - 24;
                Top = workArea.Bottom - height - (24 + stackIndex * (height + 12));
            };

            _autoDismissTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(25)
            };
            _autoDismissTimer.Tick += (s, e) =>
            {
                _autoDismissTimer.Stop();
                CloseToast();
            };
            _autoDismissTimer.Start();
        }

        private async void BtnGrant_Click(object sender, RoutedEventArgs e)
        {
            _autoDismissTimer.Stop();
            BtnGrant.IsEnabled = false;
            BtnDeny.IsEnabled = false;

            TxtActionStatus.Text = Loc.Format("Toast_GrantedStatus", Request.RequesterUser);
            TxtActionStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            TxtActionStatus.Visibility = Visibility.Visible;
            PanelButtons.Visibility = Visibility.Collapsed;

            try
            {
                BorrowNotificationService.QueueGrantAndRelinquish(Request);
            }
            catch { }

            await Task.Delay(1200);
            CloseToast();
        }

        private async void BtnDeny_Click(object sender, RoutedEventArgs e)
        {
            _autoDismissTimer.Stop();
            BtnGrant.IsEnabled = false;
            BtnDeny.IsEnabled = false;

            TxtActionStatus.Text = Loc.Tr("Toast_DeniedStatus");
            TxtActionStatus.Foreground = System.Windows.Media.Brushes.Coral;
            TxtActionStatus.Visibility = Visibility.Visible;
            PanelButtons.Visibility = Visibility.Collapsed;

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    ProjectId = Request.ProjectId,
                    RequestId = Request.RequestId,
                    Status = "Denied",
                    User = CurrentUser
                });

                await IpcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.RespondToBorrowRequest,
                    PayloadJson = payload
                }, timeoutMs: 8000);
            }
            catch { }

            await Task.Delay(1000);
            CloseToast();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _autoDismissTimer.Stop();
            CloseToast();
        }

        private void CloseToast()
        {
            try
            {
                ToastClosed?.Invoke(this, EventArgs.Empty);
                Close();
            }
            catch { }
        }
    }
}
