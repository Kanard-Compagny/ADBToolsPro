using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ADBToolsNew
{
    /// <summary>
    /// Comparateur de terminaux : deux terminaux branchés, un terminal et une référence (.adbref, terminal
    /// modèle), ou un terminal et l'un de ses états précédents (rangés dans l'historique, par numéro de série).
    /// Relevé et comparaison : Core/DeviceSnapshot.cs.
    /// </summary>
    public partial class MainWindow
    {
        private const int CompareTabIndex = 10;
        private SnapshotComparison _lastComparison;
        private DeviceSnapshot _cmpReference;
        private bool _cmpBusy;

        private async void NavCompare_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null) return;
            MainTabControl.SelectedIndex = CompareTabIndex;
            UpdateNavButtonsUI(BtnNavCompare);
            if (CmpLeftDevice.ItemsSource == null) await RefreshCompareDevicesAsync();
        }

        private async void BtnCmpRefresh_Click(object sender, RoutedEventArgs e) => await RefreshCompareDevicesAsync();

        private async Task RefreshCompareDevicesAsync()
        {
            try
            {
                var result = await _adbManager.ExecuteAsync("devices");
                var devices = (result.IsSuccess ? ParseAdbDevices(result.StandardOutput) : new List<AndroidDevice>())
                    .Where(d => d.State == "device").ToList();
                string leftSerial = (CmpLeftDevice.SelectedItem as AndroidDevice)?.Serial
                                    ?? (DeviceComboBox.SelectedItem as AndroidDevice)?.Serial;
                string rightSerial = (CmpRightDevice.SelectedItem as AndroidDevice)?.Serial;

                CmpLeftDevice.ItemsSource = devices;
                CmpRightDevice.ItemsSource = devices;
                CmpLeftDevice.SelectedItem = devices.FirstOrDefault(d => d.Serial == leftSerial) ?? devices.FirstOrDefault();
                var left = CmpLeftDevice.SelectedItem as AndroidDevice;
                CmpRightDevice.SelectedItem = devices.FirstOrDefault(d => d.Serial == rightSerial && d.Serial != left?.Serial)
                                              ?? devices.FirstOrDefault(d => d.Serial != left?.Serial);

                TxtCmpStatus.Text = devices.Count == 0
                    ? Loc.T("Aucun terminal connecté (ADB autorisé).")
                    : Loc.F("{0} terminal(aux) connecté(s).", devices.Count);
            }
            catch (Exception ex)
            {
                TxtCmpStatus.Text = Loc.F("Lecture des terminaux impossible : {0}", ex.Message);
            }
        }

        private void CmpMode_Checked(object sender, RoutedEventArgs e)
        {
            if (CmpRightDevicePanel == null) return; // pendant InitializeComponent
            CmpRightDevicePanel.Visibility = RbCmpDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            CmpRightReferencePanel.Visibility = RbCmpReference.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            CmpRightHistoryPanel.Visibility = RbCmpHistory.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (RbCmpHistory.IsChecked == true) LoadCompareHistoryStates();
        }

        private void CmpLeftDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RbCmpHistory?.IsChecked == true) LoadCompareHistoryStates();
        }

        private void LoadCompareHistoryStates()
        {
            var dev = CmpLeftDevice.SelectedItem as AndroidDevice;
            var states = dev == null ? new List<DeviceSnapshot>() : DeviceSnapshot.LoadHistory(dev.Serial);
            CmpHistoryStates.ItemsSource = states;
            CmpHistoryStates.SelectedIndex = states.Count > 0 ? 0 : -1;
            if (dev != null && states.Count == 0)
                TxtCmpStatus.Text = Loc.T("Aucun état enregistré pour ce terminal : cliquez sur « Enregistrer l'état actuel » (ou faites une première comparaison), puis revenez après l'intervention.");
        }

        private async void BtnCmpChooseReference_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Choisir une référence"),
                Filter = Loc.T("Référence ADB Tools (*.adbref)|*.adbref|Tous les fichiers (*.*)|*.*"),
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _cmpReference = DeviceSnapshot.Load(dlg.FileName);
                TxtCmpReference.Text = _cmpReference.Title;
                TxtCmpReference.ToolTip = dlg.FileName;
                TxtCmpReference.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("⚖️ Comparateur"), ex.Message, Loc.T("OK"), "", false);
            }
        }

        private async void BtnCmpSaveReference_Click(object sender, RoutedEventArgs e)
        {
            var dev = CmpLeftDevice.SelectedItem as AndroidDevice;
            if (dev == null) { await NoCompareDeviceAsync(); return; }

            var snap = await CaptureSnapshotAsync(dev.Serial);
            if (snap == null) return;
            bool ok = await ShowCustomDialogAsync(Loc.T("💾 Enregistrer comme référence"),
                Loc.T("Nom de la référence (ex. : configuration validée de l'entrepôt) :"), Loc.T("Continuer"), Loc.T("Annuler"), false,
                true, Loc.F("Référence {0}", snap.Model));
            if (!ok) return;
            snap.Name = (TxtDialogInput.Text ?? "").Trim();

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Enregistrer la référence"),
                Filter = Loc.T("Référence ADB Tools (*.adbref)|*.adbref"),
                FileName = FileNameHelper.Sanitize((snap.Name.Length > 0 ? snap.Name : "Reference_" + snap.Model) + "_" + DateTime.Now.ToString("yyyyMMdd")) + DeviceSnapshot.FileExtension,
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                snap.Save(dlg.FileName);
                _cmpReference = snap;
                TxtCmpReference.Text = snap.Title;
                TxtCmpReference.ToolTip = dlg.FileName;
                TxtCmpReference.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                TxtCmpStatus.Text = Loc.F("Référence enregistrée : {0}", dlg.FileName);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Référence enregistrée : {1}", DateTime.Now, dlg.FileName));
            }
            catch (Exception ex)
            {
                TxtCmpStatus.Text = Loc.F("Enregistrement impossible : {0}", ex.Message);
            }
        }

        private async void BtnCmpSaveState_Click(object sender, RoutedEventArgs e)
        {
            var dev = CmpLeftDevice.SelectedItem as AndroidDevice;
            if (dev == null) { await NoCompareDeviceAsync(); return; }
            var snap = await CaptureSnapshotAsync(dev.Serial);
            if (snap == null) return;
            try
            {
                string path = DeviceSnapshot.SaveToHistory(snap);
                TxtCmpStatus.Text = path == null
                    ? Loc.T("Configuration identique au dernier état enregistré : rien de nouveau à mémoriser.")
                    : Loc.F("État enregistré dans l'historique du terminal : {0}", path);
                if (RbCmpHistory.IsChecked == true) LoadCompareHistoryStates();
            }
            catch (Exception ex)
            {
                TxtCmpStatus.Text = Loc.F("Enregistrement impossible : {0}", ex.Message);
            }
        }

        private async void BtnCmpRun_Click(object sender, RoutedEventArgs e)
        {
            var left = CmpLeftDevice.SelectedItem as AndroidDevice;
            if (left == null) { await NoCompareDeviceAsync(); return; }

            DeviceSnapshot right = null;
            AndroidDevice rightDev = null;
            if (RbCmpDevices.IsChecked == true)
            {
                rightDev = CmpRightDevice.SelectedItem as AndroidDevice;
                if (rightDev == null || rightDev.Serial == left.Serial)
                {
                    await ShowCustomDialogAsync(Loc.T("⚖️ Comparateur"), Loc.T("Choisissez deux terminaux différents (branchez le second terminal puis « Actualiser les terminaux »)."), Loc.T("OK"), "", false);
                    return;
                }
            }
            else if (RbCmpReference.IsChecked == true)
            {
                right = _cmpReference;
                if (right == null)
                {
                    await ShowCustomDialogAsync(Loc.T("⚖️ Comparateur"), Loc.T("Choisissez d'abord une référence (.adbref), ou enregistrez un terminal modèle comme référence."), Loc.T("OK"), "", false);
                    return;
                }
            }
            else
            {
                right = CmpHistoryStates.SelectedItem as DeviceSnapshot;
                if (right == null)
                {
                    await ShowCustomDialogAsync(Loc.T("⚖️ Comparateur"), Loc.T("Aucun état enregistré n'est choisi pour ce terminal."), Loc.T("OK"), "", false);
                    return;
                }
            }

            var leftSnap = await CaptureSnapshotAsync(left.Serial);
            if (leftSnap == null) return;
            if (rightDev != null)
            {
                right = await CaptureSnapshotAsync(rightDev.Serial);
                if (right == null) return;
            }

            // Chaque relevé est mémorisé dans l'historique du terminal (si l'historique est activé et que la
            // configuration a changé) : c'est ce qui permet ensuite la comparaison avec un état précédent.
            if (DiagHistory.Enabled)
            {
                try
                {
                    DeviceSnapshot.SaveToHistory(leftSnap);
                    if (rightDev != null) DeviceSnapshot.SaveToHistory(right);
                }
                catch { /* historique sur un partage indisponible : la comparaison reste affichée */ }
            }

            _lastComparison = SnapshotComparer.Compare(leftSnap, right);
            TxtCmpSummary.Text = _lastComparison.Summary;
            TxtCmpSides.Text = Loc.F("À gauche : {0}\nÀ droite : {1}", leftSnap.Title, right.Title);
            BtnCmpExport.IsEnabled = true;
            ApplyCompareFilter();
            TxtCmpStatus.Text = Loc.F("Comparaison terminée à {0:HH:mm:ss}.", DateTime.Now);
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚖️ Comparaison : {1}", DateTime.Now, _lastComparison.Summary));
        }

        /// <summary>Relevé de configuration d'un terminal, avec indication de progression. Null en cas d'échec.</summary>
        private async Task<DeviceSnapshot> CaptureSnapshotAsync(string serial)
        {
            if (_cmpBusy) return null;
            _cmpBusy = true;
            BtnCmpRun.IsEnabled = BtnCmpSaveReference.IsEnabled = BtnCmpSaveState.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            TxtCmpStatus.Text = Loc.F("Relevé de la configuration de {0}… (10 à 30 secondes)", serial);
            try
            {
                var snap = await DeviceSnapshot.CaptureAsync(_adbManager, serial);
                if (snap.Items.Count == 0) throw new InvalidOperationException(Loc.T("le terminal n'a renvoyé aucune information (autorisation ADB ?)."));
                return snap;
            }
            catch (Exception ex)
            {
                TxtCmpStatus.Text = Loc.F("Relevé impossible sur {0} : {1}", serial, ex.Message);
                return null;
            }
            finally
            {
                _cmpBusy = false;
                BtnCmpRun.IsEnabled = BtnCmpSaveReference.IsEnabled = BtnCmpSaveState.IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private Task NoCompareDeviceAsync() =>
            ShowCustomDialogAsync(Loc.T("⚖️ Comparateur"), Loc.T("Connectez un terminal (ADB autorisé) puis cliquez sur « Actualiser les terminaux »."), Loc.T("OK"), "", false);

        private void CmpFilter_Changed(object sender, RoutedEventArgs e) => ApplyCompareFilter();

        private void ApplyCompareFilter()
        {
            if (CmpGrid == null || _lastComparison == null) return;
            bool showSame = ChkCmpShowSame.IsChecked == true;
            string q = (TxtCmpSearch.Text ?? "").Trim();
            CmpGrid.ItemsSource = _lastComparison.Rows
                .Where(r => showSame || r.Kind != DiffKind.Same)
                .Where(r => q.Length == 0
                            || r.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || r.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || r.Left.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                            || r.Right.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        private void BtnCmpExport_Click(object sender, RoutedEventArgs e)
        {
            if (_lastComparison == null) return;
            string a = FileNameHelper.Sanitize(_lastComparison.Left?.Serial ?? "A");
            string b = FileNameHelper.Sanitize(string.IsNullOrWhiteSpace(_lastComparison.Right?.Serial) ? "Reference" : _lastComparison.Right.Serial);
            string path = Path.Combine(GetCaptureFolderPath(), $"Comparaison_{a}_{b}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            try
            {
                File.WriteAllText(path, _lastComparison.ToCsv(ChkCmpShowSame.IsChecked == true), new System.Text.UTF8Encoding(true));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Comparaison exportée : {1}", DateTime.Now, path));
                ShowInExplorer(path);
            }
            catch (Exception ex)
            {
                TxtCmpStatus.Text = Loc.F("Export impossible : {0}", ex.Message);
            }
        }
    }
}
