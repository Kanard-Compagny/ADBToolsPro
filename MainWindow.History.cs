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
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🗂️ Ajouté à l'historique du terminal {entry.Serial}.");
            }
            catch (Exception ex)
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⚠️ Historique non enregistré ({ex.Message}). Le rapport, lui, est bien créé.");
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
                if (removed > 0) AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🗂️ Historique : {removed} fiche(s) de plus de {days} jours supprimée(s).");
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
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🗂️ Aucun historique pour {serial} : affichage de tout l'historique.");
                    serial = null;
                    entries = await Task.Run(() => DiagHistory.LoadAll(null));
                }
                DocumentWindow.ShowHistory(this, entries, serial);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync("❌ Historique illisible", ex.Message, "OK", null, true);
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
                TxtHistoryFolder.Text = custom ? s.HistoryFolder : DiagHistory.DefaultFolder + "   (à côté du programme)";
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
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⚠️ Dossier de l'historique inaccessible : {ex.Message}");
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
