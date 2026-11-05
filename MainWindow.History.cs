using ADBToolsNew.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ADBToolsNew
{
    /// <summary>
    /// Historique des diagnostics par numéro de série (voir <see cref="DiagHistory"/>) : enregistrement à chaque
    /// rapport exporté, consultation (bouton « Historique » des Diagnostics SAV, Paramètres ▸ Historique),
    /// durée de conservation appliquée au démarrage.
    /// </summary>
    public partial class MainWindow
    {
        private bool _updatingHistoryUi;

        /// <summary>Ajoute une fiche à l'historique du terminal (si l'enregistrement est activé). Jamais bloquant.</summary>
        private void RecordHistory(HistoryEntry entry, string reportPath)
        {
            if (!DiagHistory.Enabled || entry == null) return;
            try
            {
                DiagHistory.Save(entry, reportPath);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗂️ Ajouté à l'historique du terminal {1}.", DateTime.Now, entry.Serial));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Historique non enregistré ({1}). Le rapport, lui, est bien créé.", DateTime.Now, ex.Message));
            }
        }

        /// <summary>Au démarrage : retire les fiches plus anciennes que la durée choisie (en arrière-plan).</summary>
        private async Task PurgeHistoryAtStartupAsync()
        {
            int days = AppSettings.Current.HistoryRetentionDays;
            if (days <= 0) return;
            try
            {
                int removed = await Task.Run(() => DiagHistory.Purge(days));
                if (removed > 0) AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗂️ Historique : {1} fiche(s) de plus de {2} jours supprimée(s).", DateTime.Now, removed, days));
            }
            catch { }
        }

        // --------------------------------------------------------------- Consultation

        private async void BtnDiagHistory_Click(object sender, RoutedEventArgs e)
        {
            string serial = _diagSerial ?? GetSelectedValidSerial();
            await ShowHistoryAsync(serial);
        }

        private async void BtnHistoryOpenViewer_Click(object sender, RoutedEventArgs e) => await ShowHistoryAsync(null);

        private async Task ShowHistoryAsync(string serial)
        {
            try
            {
                var entries = await Task.Run(() => DiagHistory.LoadAll(serial));
                // Aucun historique pour ce terminal : on montre tout l'historique plutôt qu'une fenêtre vide.
                if (entries.Count == 0 && !string.IsNullOrWhiteSpace(serial))
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗂️ Aucun historique pour {1} : affichage de tout l'historique.", DateTime.Now, serial));
                    serial = null;
                    entries = await Task.Run(() => DiagHistory.LoadAll(null));
                }
                DocumentWindow.ShowHistory(this, entries, serial);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("❌ Historique illisible"), ex.Message, "OK", null, true);
            }
        }

        // --------------------------------------------------------------- Paramètres ▸ Historique

        private void UpdateHistorySettingsUi()
        {
            _updatingHistoryUi = true;
            try
            {
                var s = AppSettings.Current;
                ChkHistoryEnabled.IsChecked = s.HistoryEnabled;
                foreach (var child in HistoryRetentionChoices.Children)
                    if (child is RadioButton rb) rb.IsChecked = int.TryParse(rb.Tag as string, out int d) && d == s.HistoryRetentionDays;
                bool custom = !string.IsNullOrWhiteSpace(s.HistoryFolder);
                TxtHistoryFolder.Text = custom ? s.HistoryFolder : DiagHistory.DefaultFolder + Loc.T("   (à côté du programme)");
                BtnHistoryFolderReset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _updatingHistoryUi = false; }
        }

        private void ChkHistoryEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingHistoryUi || !IsLoaded) return;
            AppSettings.Current.HistoryEnabled = ChkHistoryEnabled.IsChecked == true;
            AppSettings.Save();
        }

        private void HistoryRetention_Checked(object sender, RoutedEventArgs e)
        {
            if (_updatingHistoryUi || !IsLoaded || !(sender is RadioButton rb)) return;
            if (int.TryParse(rb.Tag as string, out int days))
            {
                AppSettings.Current.HistoryRetentionDays = days;
                AppSettings.Save();
            }
        }

        private void BtnHistoryFolderChange_Click(object sender, RoutedEventArgs e)
        {
            string folder = PickFolder("Dossier de l'historique (un dossier réseau permet de le partager avec l'équipe)", DiagHistory.Folder);
            if (folder == null) return;
            AppSettings.Current.HistoryFolder = folder;
            AppSettings.Save();
            UpdateHistorySettingsUi();
        }

        private void BtnHistoryFolderOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(DiagHistory.Folder);
                Process.Start(new ProcessStartInfo(DiagHistory.Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Dossier de l'historique inaccessible : {1}", DateTime.Now, ex.Message));
            }
        }

        private void BtnHistoryFolderReset_Click(object sender, RoutedEventArgs e)
        {
            AppSettings.Current.HistoryFolder = "";
            AppSettings.Save();
            UpdateHistorySettingsUi();
        }
    }
}
