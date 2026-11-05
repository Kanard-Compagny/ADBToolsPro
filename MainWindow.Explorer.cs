using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using System.Text;
using System.Text.Json;
using LiveCharts;
using LiveCharts.Wpf;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ADBToolsNew
{
    public partial class MainWindow : Window
    {

        // ==========================================
        // MODULE EXPLORATEUR DE FICHIERS
        // ==========================================
        private readonly FileExplorerService _fileExplorerService;
        private string _currentExplorerPath = "/sdcard/";
        private List<AndroidFileItem> _currentFiles = new List<AndroidFileItem>();
        private System.Threading.CancellationTokenSource _transferCts;

        private void BtnQuickPath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path)
            {
                TxtCurrentPath.Text = path;
                LoadCurrentFolderAsync();
            }
        }

        private void ShowTransferProgress(string label)
        {
            TransferProgressPanel.Visibility = Visibility.Visible;
            TxtTransferStatus.Text = label;
            PbTransfer.Value = 0;
            BtnFilePush.IsEnabled = false;
            BtnFilePull.IsEnabled = false;
        }

        private void HideTransferProgress()
        {
            TransferProgressPanel.Visibility = Visibility.Collapsed;
            BtnFilePush.IsEnabled = true;
            BtnFilePull.IsEnabled = true;
        }

        private void BtnCancelTransfer_Click(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

        private async void LoadCurrentFolderAsync()
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                FilesDataGrid.ItemsSource = null;
                return;
            }

            // Assainissement du chemin
            string path = TxtCurrentPath.Text.Trim();
            if (!path.EndsWith("/")) path += "/";
            _currentExplorerPath = path;
            TxtCurrentPath.Text = path;

            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📂 Lecture du répertoire : {_currentExplorerPath}...");

            try
            {
                _currentFiles = await _fileExplorerService.ListDirectoryAsync(dev.Serial, _currentExplorerPath);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    FilesDataGrid.ItemsSource = _currentFiles;
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole($"\n[ERREUR] {ex.Message}"); });
            }
        }

        // --- NAVIGATION UI EXPLORATEUR ---
        private void BtnFileRefresh_Click(object sender, RoutedEventArgs e) => LoadCurrentFolderAsync();

        private void TxtCurrentPath_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) LoadCurrentFolderAsync();
        }

        private void BtnFileUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentExplorerPath == "/") return; // On est déjà à la racine absolue

            // Supprime le dernier slash s'il existe
            string temp = _currentExplorerPath.TrimEnd('/');
            int lastSlashIndex = temp.LastIndexOf('/');

            if (lastSlashIndex >= 0)
            {
                TxtCurrentPath.Text = temp.Substring(0, lastSlashIndex + 1);
                LoadCurrentFolderAsync();
            }
        }

        private void FilesDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var selectedItem = FilesDataGrid.SelectedItem as AndroidFileItem;
            if (selectedItem != null && selectedItem.IsDirectory)
            {
                TxtCurrentPath.Text = _currentExplorerPath + selectedItem.Name + "/";
                LoadCurrentFolderAsync();
            }
        }

        // --- ACTIONS SUR LES FICHIERS ---
        // La construction des commandes et l'échappement des arguments vivent maintenant dans
        // Core/FileExplorerService.cs — cette classe ne fait plus qu'orchestrer l'UI.

        // HELPER POUR LE MULTI-SÉLECTION
        private List<AndroidFileItem> GetSelectedFiles()
        {
            if (FilesDataGrid.SelectedItems == null) return new List<AndroidFileItem>();
            return FilesDataGrid.SelectedItems.Cast<AndroidFileItem>().ToList();
        }

        private async void BtnFilePush_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Sélectionnez les fichiers à envoyer vers le terminal",
                Multiselect = true // ✅ MULTI-SÉLECTION ACTIVÉE
            };

            if (dlg.ShowDialog() != true) return;

            _transferCts = new System.Threading.CancellationTokenSource();
            int total = dlg.FileNames.Length;

            for (int i = 0; i < total; i++)
            {
                string file = dlg.FileNames[i];
                string fileName = System.IO.Path.GetFileName(file);
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📤 PUSH : Envoi de '{fileName}'...");
                ShowTransferProgress($"Envoi {i + 1}/{total} : {fileName}");

                var transfer = new AdbTransferProcess();
                var (success, _) = await transfer.RunAsync(_adbExePath,
                    $"-s {dev.Serial} push {AdbManager.QuoteLocalArg(file)} {AdbManager.QuoteLocalArg(_currentExplorerPath)}",
                    pct => Application.Current.Dispatcher.Invoke(() => PbTransfer.Value = pct),
                    _transferCts.Token);

                if (_transferCts.IsCancellationRequested)
                {
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⛔ Transfert annulé.");
                    break;
                }
                AppendConsole(success
                    ? $"\n[{DateTime.Now:HH:mm:ss}] ✅ '{fileName}' envoyé."
                    : $"\n[{DateTime.Now:HH:mm:ss}] ❌ Échec pour '{fileName}'.");
            }

            HideTransferProgress();
            LoadCurrentFolderAsync(); // Même annulé en cours de route, certains fichiers ont pu passer avant : on rafraîchit dans tous les cas.
        }

        private async void BtnFilePull_Click(object sender, RoutedEventArgs e)
        {
            var files = GetSelectedFiles();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (files.Count == 0 || dev == null) return;

            if (files.Count == 1 && !files[0].IsDirectory)
            {
                var file = files[0];
                var dlg = new Microsoft.Win32.SaveFileDialog { FileName = file.Name, Title = "Enregistrer le fichier sous..." };
                if (dlg.ShowDialog() != true) return;

                _transferCts = new System.Threading.CancellationTokenSource();
                string remotePath = _currentExplorerPath + file.Name;
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📥 PULL : Téléchargement de {file.Name}...");
                ShowTransferProgress($"Récupération : {file.Name}");

                var transfer = new AdbTransferProcess();
                var (success, _) = await transfer.RunAsync(_adbExePath,
                    $"-s {dev.Serial} pull {AdbManager.QuoteLocalArg(remotePath)} {AdbManager.QuoteLocalArg(dlg.FileName)}",
                    pct => Application.Current.Dispatcher.Invoke(() => PbTransfer.Value = pct),
                    _transferCts.Token);

                HideTransferProgress();
                AppendConsole(_transferCts.IsCancellationRequested
                    ? $"\n[{DateTime.Now:HH:mm:ss}] ⛔ Transfert annulé."
                    : (success ? $"\n[{DateTime.Now:HH:mm:ss}] ✅ Fichier récupéré !" : $"\n[{DateTime.Now:HH:mm:ss}] ❌ Échec du transfert."));
            }
            else
            {
                // Vrai sélecteur de dossier de Windows (bouton « Sélectionner un dossier »).
                // Note : si l'élément sélectionné est un dossier, "adb pull" le récupère récursivement
                // avec tout son contenu — aucune logique supplémentaire n'est nécessaire pour ça.
                string destFolder = ADBToolsNew.Core.FolderPicker.Pick(this, "Dossier de destination");
                if (destFolder == null) return;
                _transferCts = new System.Threading.CancellationTokenSource();
                int total = files.Count;

                for (int i = 0; i < total; i++)
                {
                    var file = files[i];
                    string remotePath = _currentExplorerPath + file.Name;
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📥 PULL : Téléchargement de {file.Name} vers {destFolder}...");
                    ShowTransferProgress($"Récupération {i + 1}/{total} : {file.Name}");

                    var transfer = new AdbTransferProcess();
                    var (success, _) = await transfer.RunAsync(_adbExePath,
                        $"-s {dev.Serial} pull {AdbManager.QuoteLocalArg(remotePath)} {AdbManager.QuoteLocalArg(destFolder)}",
                        pct => Application.Current.Dispatcher.Invoke(() => PbTransfer.Value = pct),
                        _transferCts.Token);

                    if (_transferCts.IsCancellationRequested)
                    {
                        AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⛔ Transfert annulé.");
                        break;
                    }
                    AppendConsole(success
                        ? $"\n[{DateTime.Now:HH:mm:ss}] ✅ Terminé pour {file.Name} !"
                        : $"\n[{DateTime.Now:HH:mm:ss}] ❌ Échec pour {file.Name}.");
                }

                HideTransferProgress();
                OpenFolder(destFolder);
            }
        }

        private async void BtnNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            bool isConfirmed = await ShowCustomDialogAsync("Nouveau Dossier", "Entrez le nom du nouveau dossier :", "Créer", "Annuler", false, true, "Nouveau_Dossier");

            if (isConfirmed)
            {
                string newName = TxtDialogInput.Text.Trim();
                if (!string.IsNullOrEmpty(newName))
                {
                    string fullPath = _currentExplorerPath + newName;
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📁 Création du dossier {newName}...");

                    ReportAdbActionResult("Création du dossier", newName, await _fileExplorerService.CreateFolderAsync(dev.Serial, fullPath));
                    LoadCurrentFolderAsync();
                }
            }
        }

        private async void BtnRenameFile_Click(object sender, RoutedEventArgs e)
        {
            var files = GetSelectedFiles();
            if (files.Count != 1)
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⚠️ Veuillez sélectionner un seul élément à renommer.");
                return;
            }

            var file = files[0];
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            bool isConfirmed = await ShowCustomDialogAsync("Renommer", $"Renommer l'élément '{file.Name}' en :", "Valider", "Annuler", false, true, file.Name);

            if (isConfirmed)
            {
                string newName = TxtDialogInput.Text.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != file.Name)
                {
                    string oldPath = _currentExplorerPath + file.Name;
                    string newPath = _currentExplorerPath + newName;

                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ✏️ Renommage de {file.Name} en {newName}...");

                    var res = await _fileExplorerService.RenameAsync(dev.Serial, oldPath, newPath);
                    ReportAdbActionResult("Renommage", file.Name, res);

                    LoadCurrentFolderAsync();
                }
            }
        }

        private async void BtnDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            var files = GetSelectedFiles();
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (files.Count == 0 || dev == null) return;

            string msg = files.Count == 1 ? $"Voulez-vous supprimer '{files[0].Name}' ?" : $"Voulez-vous supprimer ces {files.Count} éléments ?";
            bool isConfirmed = await ShowCustomDialogAsync("Suppression Irréversible", msg, "Supprimer", "Annuler", true);

            if (isConfirmed)
            {
                foreach (var file in files)
                {
                    string remotePath = _currentExplorerPath + file.Name;
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🗑️ Suppression de {file.Name}...");

                    ReportAdbActionResult("Suppression", file.Name, await _fileExplorerService.DeleteAsync(dev.Serial, remotePath));
                }
                LoadCurrentFolderAsync();
            }
        }
    }
}
