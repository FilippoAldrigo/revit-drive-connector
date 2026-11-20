using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
    public partial class BorrowCenterWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;
        private readonly NamedPipeIpcClient _ipcClient = new();
        private readonly string _projectId;
        private readonly string _username;

        public ObservableCollection<BorrowRequest> IncomingRequests { get; } = new();
        public ObservableCollection<BorrowRequest> OutgoingRequests { get; } = new();

        public BorrowCenterWindow(UIApplication uiapp)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _uiapp = uiapp;
            _doc = uiapp.ActiveUIDocument.Document;
            _username = _doc.Application.Username;

            string centralPath = SynchronizationCoordinator.GetCentralModelPath(_doc);
            _projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);

            LvIncoming.ItemsSource = IncomingRequests;
            LvOutgoing.ItemsSource = OutgoingRequests;

            ApplyLocalization();

            Loaded += async (s, e) => await LoadRequestsAsync();
        }

        private void ApplyLocalization()
        {
            Title = Loc.Tr("Borrow_Title");
            TxtHeaderTitle.Text = Loc.Tr("Borrow_Header");
            TxtHeaderSubtitle.Text = Loc.Tr("Borrow_Subtitle");
            BtnRefresh.Content = Loc.Tr("Borrow_BtnRefresh");

            TabIncoming.Header = Loc.Tr("Borrow_TabIncoming");
            TabOutgoing.Header = Loc.Tr("Borrow_TabOutgoing");

            ColIncColleague.Header = Loc.Tr("Borrow_ColColleague");
            ColIncElement.Header = Loc.Tr("Borrow_ColElement");
            ColIncMachine.Header = Loc.Tr("Borrow_ColMachine");
            ColIncDate.Header = Loc.Tr("Borrow_ColDate");

            BtnDeny.Content = Loc.Tr("Borrow_BtnDeny");
            BtnGrant.Content = Loc.Tr("Borrow_BtnGrant");

            ColOutOwner.Header = Loc.Tr("Borrow_ColOwner");
            ColOutElement.Header = Loc.Tr("Borrow_ColElement");
            ColOutStatus.Header = Loc.Tr("Borrow_ColStatus");
            ColOutDate.Header = Loc.Tr("Borrow_ColDate");

            BtnCheckout.Content = Loc.Tr("Borrow_BtnCheckout");
            BtnRelinquish.Content = Loc.Tr("Borrow_BtnRelinquish");
            BtnClose.Content = Loc.Tr("Common_Close");
        }

        private async Task LoadRequestsAsync()
        {
            IncomingRequests.Clear();
            OutgoingRequests.Clear();

            try
            {
                var payload = JsonSerializer.Serialize(new { ProjectId = _projectId, User = _username });

                var respInc = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.GetPendingBorrowRequests,
                    PayloadJson = payload
                });

                if (respInc.Success && !string.IsNullOrEmpty(respInc.DataJson))
                {
                    var list = JsonSerializer.Deserialize<List<BorrowRequest>>(respInc.DataJson);
                    if (list != null)
                    {
                        foreach (var item in list) IncomingRequests.Add(item);
                    }
                }

                var respOut = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.GetOutgoingBorrowRequests,
                    PayloadJson = payload
                });

                if (respOut.Success && !string.IsNullOrEmpty(respOut.DataJson))
                {
                    var list = JsonSerializer.Deserialize<List<BorrowRequest>>(respOut.DataJson);
                    if (list != null)
                    {
                        foreach (var item in list) OutgoingRequests.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadRequestsAsync();
        }

        private async void BtnGrant_Click(object sender, RoutedEventArgs e)
        {
            var selected = LvIncoming.SelectedItem as BorrowRequest;
            if (selected == null)
            {
                MessageBox.Show(Loc.Tr("Borrow_SelectPrompt"), Loc.Tr("Borrow_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnGrant.IsEnabled = false;
            BtnDeny.IsEnabled = false;
            try
            {
                // 1. Acquisisce lock, allinea da Drive, rilascia effettivamente l'elemento e carica in sicurezza
                if (!BorrowNotificationService.RelinquishAndGrantImmediate(_doc, selected, _username, out string err))
                {
                    MessageBox.Show($"Impossibile completare il rilascio sicuro su Google Drive:\n{err}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. Aggiorna lo stato della richiesta su Google Drive
                var payload = JsonSerializer.Serialize(new
                {
                    ProjectId = _projectId,
                    RequestId = selected.RequestId,
                    Status = "Granted",
                    User = _username
                });

                var resp = await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.RespondToBorrowRequest,
                    PayloadJson = payload
                });

                if (resp.Success)
                {
                    MessageBox.Show(Loc.Format("Borrow_GrantedSuccess", selected.RequesterUser), Loc.Tr("Ribbon_TabName"), MessageBoxButton.OK, MessageBoxImage.Information);
                    await LoadRequestsAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnGrant.IsEnabled = true;
                BtnDeny.IsEnabled = true;
            }
        }

        private async void BtnDeny_Click(object sender, RoutedEventArgs e)
        {
            var selected = LvIncoming.SelectedItem as BorrowRequest;
            if (selected == null) return;

            BtnGrant.IsEnabled = false;
            BtnDeny.IsEnabled = false;
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    ProjectId = _projectId,
                    RequestId = selected.RequestId,
                    Status = "Denied",
                    User = _username
                });

                await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.RespondToBorrowRequest,
                    PayloadJson = payload
                });

                await LoadRequestsAsync();
            }
            catch { }
            finally
            {
                BtnGrant.IsEnabled = true;
                BtnDeny.IsEnabled = true;
            }
        }

        private async void BtnCheckoutApproved_Click(object sender, RoutedEventArgs e)
        {
            var approved = OutgoingRequests.Where(r => r.Status == BorrowRequestStatus.Granted).ToList();
            if (approved.Count == 0)
            {
                MessageBox.Show(Loc.Tr("Borrow_NoApproved"), Loc.Tr("Borrow_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BtnCheckout.IsEnabled = false;
            try
            {
                // 1. Acquisisce lock, allinea da Drive, ricarica e prende in carico gli elementi approvati
                var requestedIds = approved.Select(a => new ElementId(a.ElementId)).ToList();
                int checkedOutCount = 0;
                bool ok = SynchronizationCoordinator.ExecuteLockedCentralOperation(_doc, () =>
                {
                    var validIds = requestedIds.Where(id => _doc.GetElement(id) != null).ToList();
                    if (validIds.Count == 0)
                    {
                        return false;
                    }

                    using (var trans = new Transaction(_doc, "Checkout approved elements"))
                    {
                        trans.Start();
                        WorksharingUtils.CheckoutElements(_doc, validIds);
                        trans.Commit();
                    }
                    checkedOutCount = validIds.Count;
                    return true;
                }, out string err);

                if (!ok)
                {
                    MessageBox.Show($"Impossibile completare la presa in carico sicura:\n{err}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 2. Segna le richieste come completate
                string completedIds = string.Join(",", approved.Select(a => a.RequestId));
                await _ipcClient.SendRequestAsync(new IpcRequest
                {
                    Command = IpcCommand.MarkBorrowRequestCompleted,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        ProjectId = _projectId,
                        RequestIds = completedIds
                    })
                });

                await LoadRequestsAsync();
                MessageBox.Show(Loc.Format("Borrow_CheckoutSuccess", checkedOutCount), Loc.Tr("Ribbon_TabName"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCheckout.IsEnabled = true;
            }
        }

        private void BtnRelinquishAll_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(Loc.Tr("Borrow_ConfirmRelinquish"),
                Loc.Tr("Borrow_BtnRelinquish"), MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            BtnRelinquish.IsEnabled = false;
            try
            {
                bool ok = SynchronizationCoordinator.ExecuteLockedCentralOperation(_doc, () =>
                {
                    var relOptions = new RelinquishOptions(true)
                    {
                        StandardWorksets = true,
                        ViewWorksets = true,
                        FamilyWorksets = true,
                        UserWorksets = true,
                        CheckedOutElements = true
                    };

                    var transactOptions = new TransactWithCentralOptions();
                    WorksharingUtils.RelinquishOwnership(_doc, relOptions, transactOptions);
                    return true;
                }, out string err);

                if (ok)
                {
                    MessageBox.Show(Loc.Tr("Borrow_RelinquishSuccess"), Loc.Tr("Ribbon_TabName"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Impossibile rilasciare e sincronizzare in sicurezza su Google Drive:\n{err}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnRelinquish.IsEnabled = true;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
