using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;
using RevitDriveConnector.Core.IPC;
using RevitDriveConnector.Core.Localization;

namespace RevitDriveConnector.Addin.UI
{
    public partial class ManageLinksWindow : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;
        private static readonly NamedPipeIpcClient IpcClient = new();

        public ManageLinksWindow(UIApplication uiapp)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _uiapp = uiapp;
            _doc = uiapp.ActiveUIDocument.Document;

            ApplyLocalization();

            Loaded += (s, e) => RefreshLinks();
        }

        private void ApplyLocalization()
        {
            Title = Loc.Tr("Links_Title");
            TxtHeaderTitle.Text = Loc.Tr("Links_Header");
            TxtHeaderSubtitle.Text = Loc.Tr("Links_Subtitle");
            TxtAlertMessage.Text = Loc.Tr("Links_Alert");

            ColName.Header = Loc.Tr("Links_ColName");
            ColType.Header = Loc.Tr("Links_ColType");
            ColPath.Header = Loc.Tr("Links_ColPath");
            ColStatus.Header = Loc.Tr("Links_ColStatus");

            BtnRefresh.Content = Loc.Tr("Links_BtnRefresh");
            BtnClose.Content = Loc.Tr("Common_Close");
            BtnConsolidate.Content = Loc.Tr("Links_BtnConsolidate");
        }

        private void RefreshLinks()
        {
            try
            {
                var links = ExternalLinkManagerService.GetProjectLinks(_doc);
                LvLinks.ItemsSource = links;

                int nonCompliant = links.Count(l => !l.IsInsideDriveOrRelative || l.PathType == PathType.Absolute || l.Status == LinkedFileStatus.NotFound);

                if (nonCompliant > 0)
                {
                    AlertBanner.Visibility = System.Windows.Visibility.Visible;
                    TxtAlertMessage.Text = Loc.Format("Links_NonCompliant", nonCompliant);
                    BtnConsolidate.IsEnabled = true;
                }
                else
                {
                    AlertBanner.Visibility = System.Windows.Visibility.Collapsed;
                    TxtStatus.Text = Loc.Format("Links_AllCompliant", links.Count);
                    TxtStatus.Foreground = System.Windows.Media.Brushes.Green;
                    BtnConsolidate.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"{Loc.Tr("Common_Error")}: {ex.Message}";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Coral;
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshLinks();
        }

        private async void BtnConsolidate_Click(object sender, RoutedEventArgs e)
        {
            BtnConsolidate.IsEnabled = false;
            TxtStatus.Text = "Consolidating links to Drive...";
            TxtStatus.Foreground = System.Windows.Media.Brushes.DodgerBlue;

            try
            {
                var (consolidated, failed, errors) = ExternalLinkManagerService.ConsolidateAndRelativizeLinks(_doc, msg =>
                {
                    TxtStatus.Text = msg;
                });

                if (consolidated > 0 && _doc.IsWorkshared)
                {
                    string centralPath = SynchronizationCoordinator.GetCentralModelPath(_doc);
                    if (SynchronizationCoordinator.IsDriveManagedPath(centralPath))
                    {
                        string projectId = SynchronizationCoordinator.ExtractProjectName(centralPath);
                        string linksDir = System.IO.Path.Combine(ExternalLinkManagerService.GetProjectBaseDirectory(_doc), "Links");
                        if (System.IO.Directory.Exists(linksDir))
                        {
                            TxtStatus.Text = "Caricamento collegamenti su Google Drive in corso...";
                            var upResp = await IpcClient.SendRequestAsync(new IpcRequest
                            {
                                Command = IpcCommand.UploadProjectLinks,
                                PayloadJson = JsonSerializer.Serialize(new
                                {
                                    ProjectId = projectId,
                                    LinksDir = linksDir
                                })
                            }, timeoutMs: 120000);

                            if (!upResp.Success)
                            {
                                failed++;
                                errors.Add($"Upload collegamenti su Google Drive non riuscito: {upResp.ErrorMessage}");
                            }
                        }
                    }
                }

                if (failed == 0)
                {
                    MessageBox.Show(Loc.Format("Links_SuccessMsg", consolidated),
                        Loc.Tr("Ribbon_TabName"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    string errStr = string.Join("\n- ", errors.Take(3));
                    MessageBox.Show($"Consolidated {consolidated} links with {failed} warnings:\n- {errStr}",
                        Loc.Tr("Ribbon_TabName"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                RefreshLinks();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{Loc.Tr("Common_Error")}: {ex.Message}", Loc.Tr("Common_Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                BtnConsolidate.IsEnabled = true;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
