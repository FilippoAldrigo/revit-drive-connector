using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDriveConnector.Addin.Services;

namespace RevitDriveConnector.Addin.UI
{
    public partial class LiveEditsWindow : Window
    {
        private readonly UIDocument _uidoc;
        private readonly Document _doc;
        private bool _isHighlighted = false;

        public LiveEditsWindow(UIDocument uidoc)
        {
            InitializeComponent();
            IconHelper.ApplyWindowIcon(this);
            _uidoc = uidoc ?? throw new ArgumentNullException(nameof(uidoc));
            _doc = uidoc.Document;

            Loaded += (s, e) => RefreshData();
        }

        private void RefreshData()
        {
            try
            {
                var items = VisualLocksService.GetActiveLockedElements(_doc);
                LstLocks.ItemsSource = items;

                int remoteCount = items.Count(i => i.IsRemote);
                int localCount = items.Count(i => !i.IsRemote);
                TxtSubtitle.Text = $"Trovati {items.Count} elementi in modifica ({remoteCount} dai colleghi, {localCount} da te).";

                BtnSelect.IsEnabled = items.Count > 0;
                BtnIsolate.IsEnabled = items.Count > 0;
                BtnHighlight.IsEnabled = items.Count > 0;
            }
            catch (Exception ex)
            {
                TxtSubtitle.Text = $"Errore caricamento lock: {ex.Message}";
            }
        }

        private void LstLocks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            BtnSelect.IsEnabled = LstLocks.SelectedItem is LockedElementDisplayItem;
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (LstLocks.SelectedItem is LockedElementDisplayItem item && item.Id != ElementId.InvalidElementId)
            {
                try
                {
                    _uidoc.Selection.SetElementIds(new[] { item.Id });
                    _uidoc.ShowElements(item.Id);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Impossibile mostrare l'elemento: {ex.Message}", "Drive Connector", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void BtnIsolate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isolated = VisualLocksService.IsolateLocks(_uidoc, remoteOnly: false);
                if (isolated)
                {
                    MessageBox.Show("Elementi in modifica isolati temporaneamente nella vista corrente.", "Visual Locks", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Nessun elemento attualmente in modifica da isolare.", "Visual Locks", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore isolamento elementi: {ex.Message}", "Visual Locks", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnHighlight_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _isHighlighted = !_isHighlighted;
                bool ok = VisualLocksService.HighlightLocks(_doc, _uidoc.ActiveView, _isHighlighted);
                if (ok && _isHighlighted)
                {
                    BtnHighlight.Content = "Rimuovi Colori";
                }
                else
                {
                    BtnHighlight.Content = "Evidenzia Colori";
                    _isHighlighted = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Errore override colori: {ex.Message}", "Visual Locks", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnResetView_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VisualLocksService.ResetView(_uidoc);
                _isHighlighted = false;
                BtnHighlight.Content = "Evidenzia Colori";
            }
            catch { }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshData();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
