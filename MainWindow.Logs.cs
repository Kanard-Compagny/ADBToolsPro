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
        // === ONGLET 6 : LOGS & BUGREPORT (N3) =====
        // ==========================================

        private System.Collections.ObjectModel.ObservableCollection<LogEntry> _displayLogs = new System.Collections.ObjectModel.ObservableCollection<LogEntry>();
        private List<LogEntry> _masterLogs = new List<LogEntry>(10000);
        private readonly LogcatService _logcatService = new LogcatService();
        private string _targetForegroundPid = "";
        private bool _packageFilterActive = false;
        private const int MAX_LOGS = 10000;
        private const int RAW_VIEW_MAX_LINES = 1000;
        private const int LOG_EVICTION_CHUNK = 1000;
        private DispatcherTimer _logUiTimer;

        private void ToggleLogcatRead_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                ToggleLogcatRead.IsChecked = false;
                return;
            }

            if (ToggleLogcatRead.IsChecked == true)
            {
                // DÉMARRAGE DE LA LECTURE
                ToggleLogcatRead.Content = "⏸️ Pause";
                StartLogcat(dev.Serial);

                // LANCE LA PULSATION DU BOUTON LOGS DANS LE MENU DE GAUCHE
                AnimateNavButton(BtnNavLogs, true);
            }
            else
            {
                // MISE EN PAUSE
                ToggleLogcatRead.Content = "▶️ Démarrer";
                StopLogcat();

                // ARRÊTE LA PULSATION
                AnimateNavButton(BtnNavLogs, false);
            }
        }

        private void StartLogcat(string serial)
        {
            StopLogcat(); // Sécurité

            // Chronomètre qui videra le tampon du service dans l'UI toutes les 500ms
            _logUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _logUiTimer.Tick += (s, ev) => ProcessLogBuffer();
            _logUiTimer.Start();

            try
            {
                _logcatService.Start(_adbExePath, serial);
            }
            catch (Exception ex) { AppendConsole($"\n❌ Erreur Logcat : {ex.Message}"); }
        }

        // La méthode qui s'exécute toutes les 500ms pour rafraîchir l'écran
        private void ProcessLogBuffer()
        {
            if (!_logcatService.IsRunning) return;

            List<LogEntry> batch = _logcatService.DrainBuffer();
            if (batch.Count == 0) return;

            bool addedToDisplay = false;
            StringBuilder rawBatch = new StringBuilder(); // Très performant pour assembler du texte
            LogFilterCriteria criteria = BuildFilterCriteria();

            foreach (var entry in batch)
            {
                _masterLogs.Add(entry);

                if (LogFilter.Matches(entry, criteria))
                {
                    _displayLogs.Add(entry);
                    addedToDisplay = true;

                    if (RbViewRaw?.IsChecked == true)
                    {
                        rawBatch.AppendLine(entry.RawLine);
                    }
                }
            }

            // Nettoyage de la mémoire au-delà de MAX_LOGS, PAR BLOC de LOG_EVICTION_CHUNK lignes : auparavant un
            // RemoveAt(0) par ligne en trop, à chaque lot de 500 ms, décalait toute la liste et déclenchait une
            // notification du tableau par ligne — l'UI gelait dès que le logcat devenait bavard.
            bool evicted = _masterLogs.Count > MAX_LOGS || _displayLogs.Count > MAX_LOGS;
            if (_masterLogs.Count > MAX_LOGS)
                _masterLogs.RemoveRange(0, _masterLogs.Count - (MAX_LOGS - LOG_EVICTION_CHUNK));
            if (_displayLogs.Count > MAX_LOGS)
                ReplaceDisplayLogs(_displayLogs.Skip(_displayLogs.Count - (MAX_LOGS - LOG_EVICTION_CHUNK)));

            // Mise à jour de l'UI
            UpdateLogStatsText();

            if (RbViewRaw?.IsChecked == true)
            {
                if (evicted)
                    // Le texte brut doit suivre la même éviction que _displayLogs, sinon il grossit sans
                    // limite sur une session longue (contrairement à la liste structurée, plafonnée).
                    RebuildRawLogsText();
                else if (rawBatch.Length > 0)
                    TxtLogsRaw.AppendText(rawBatch.ToString());
            }

            // On scroll UNE SEULE FOIS pour tout le bloc !
            if (addedToDisplay && ChkAutoScroll?.IsChecked == true)
            {
                if (RbViewTable?.IsChecked == true && _displayLogs.Count > 0)
                    LogsDataGrid.ScrollIntoView(_displayLogs[_displayLogs.Count - 1]);
                else if (RbViewRaw?.IsChecked == true)
                    LogsRawScroll.ScrollToEnd();
            }
        }

        private void StopLogcat()
        {
            // Arrêt du chronomètre d'interface
            if (_logUiTimer != null)
            {
                _logUiTimer.Stop();
                _logUiTimer = null;
            }

            _logcatService.Stop();

            AnimateNavButton(BtnNavLogs, false);
        }

        // --- FILTRES ---
        // Capture l'état des contrôles UNE fois par lot plutôt qu'à chaque ligne (voir Core/LogFilter.cs
        // pour la logique de correspondance elle-même, pure et testable indépendamment de la UI).
        private LogFilterCriteria BuildFilterCriteria()
        {
            string level = null;
            if (RbLogDebug?.IsChecked == true) level = "D";
            else if (RbLogInfo?.IsChecked == true) level = "I";
            else if (RbLogWarn?.IsChecked == true) level = "W";
            else if (RbLogError?.IsChecked == true) level = "E";

            string pid = _packageFilterActive ? _targetForegroundPid : null;

            return new LogFilterCriteria
            {
                LevelFilter = level,
                ForegroundPid = pid,
                SearchText = TxtLogSearch?.Text?.Trim(),
                UseRegex = ToggleLogRegex?.IsChecked == true
            };
        }

        private void ApplyFiltersToExistingLogs()
        {
            // Une seule réinitialisation du tableau au lieu de 10 000 ajouts notifiés un par un.
            LogFilterCriteria criteria = BuildFilterCriteria();
            ReplaceDisplayLogs(_masterLogs.Where(log => LogFilter.Matches(log, criteria)));

            if (RbViewRaw?.IsChecked == true) RebuildRawLogsText();

            UpdateLogStatsText();
            if (ChkAutoScroll?.IsChecked == true && RbViewRaw?.IsChecked == true) LogsRawScroll.ScrollToEnd();
        }

        /// <summary>
        /// Reconstruit la vue "Données Brutes" depuis les RAW_VIEW_MAX_LINES dernières entrées de
        /// _displayLogs (elle-même déjà plafonnée à MAX_LOGS) plutôt que la liste entière : une TextBox WPF
        /// classique n'est pas virtualisée comme le DataGrid du Tableau, donc réécrire d'un coup jusqu'à
        /// 10 000 lignes dans TxtLogsRaw.Text force une mise en page complète qui gèle l'UI un instant. Le
        /// Tableau (recherche/filtres) et la Sauvegarde .txt gardent eux l'accès aux données complètes —
        /// seul cet affichage brut à l'écran est raccourci.
        /// </summary>
        private void RebuildRawLogsText()
        {
            var linesToShow = _displayLogs.Count > RAW_VIEW_MAX_LINES
                ? _displayLogs.Skip(_displayLogs.Count - RAW_VIEW_MAX_LINES)
                : (IEnumerable<LogEntry>)_displayLogs;

            var sb = new StringBuilder();
            foreach (var log in linesToShow) sb.AppendLine(log.RawLine);
            TxtLogsRaw.Text = sb.ToString();
        }

        /// <summary>Centralise le texte de statistiques, y compris la mention de troncature de la vue brute.</summary>
        private void UpdateLogStatsText()
        {
            string text = $"Lignes affichées : {_displayLogs.Count} / {MAX_LOGS}";
            if (RbViewRaw?.IsChecked == true && _displayLogs.Count > RAW_VIEW_MAX_LINES)
                text += $" — vue brute limitée aux {RAW_VIEW_MAX_LINES} dernières lignes (utilisez le Tableau + Recherche pour remonter plus loin)";
            TxtLogStats.Text = text;
        }

        /// <summary>Remplace le contenu affiché en une seule opération (une seule notification au tableau).</summary>
        private void ReplaceDisplayLogs(IEnumerable<LogEntry> entries)
        {
            _displayLogs = new System.Collections.ObjectModel.ObservableCollection<LogEntry>(entries);
            if (LogsDataGrid.ItemsSource != null) LogsDataGrid.ItemsSource = _displayLogs;
        }

        private void LogFilter_Changed(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyFiltersToExistingLogs(); }

        // La recherche n'est appliquée qu'après 250 ms sans frappe : auparavant chaque caractère refiltrait les
        // 10 000 lignes (et recompilait la regex), ce qui rendait la saisie saccadée.
        private DispatcherTimer _logSearchDebounce;

        private void TxtLogSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_logSearchDebounce == null)
            {
                _logSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _logSearchDebounce.Tick += (s, ev) => { _logSearchDebounce.Stop(); ApplyFiltersToExistingLogs(); };
            }
            _logSearchDebounce.Stop();
            _logSearchDebounce.Start();
        }

        // Détection de l'app au premier plan : voir Core/ForegroundAppService.cs (partagé avec le sniffer
        // du Package Manager, qui utilisait auparavant ces mêmes méthodes définies ici en silence).

        // --- CIBLAGE PAR NOM DE PACKAGE : un seul champ, soit pré-rempli par détection automatique de
        // l'app affichée à l'écran, soit saisi directement quand on connaît déjà le nom exact (utile pour
        // un service en arrière-plan qui n'est jamais au premier plan). ---
        private async void TxtPackageFilter_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await ApplyPackageFilterAsync(TxtPackageFilter?.Text?.Trim());
            TogglePackageFilter.IsChecked = _packageFilterActive;
        }

        private async void BtnDetectForeground_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            TxtPackageFilter.ToolTip = "Détection en cours...";
            string pkg = await _foregroundAppService.GetForegroundPackageNameAsync(dev.Serial);

            if (string.IsNullOrEmpty(pkg))
            {
                TxtPackageFilter.ToolTip = "Impossible de détecter l'app affichée";
                return;
            }

            TxtPackageFilter.Text = pkg;
            await ApplyPackageFilterAsync(pkg);
            TogglePackageFilter.IsChecked = _packageFilterActive;
        }

        // Le bouton 🔎 est un ToggleButton pour refléter visuellement l'état actif/inactif du filtre :
        // WPF a déjà basculé IsChecked au moment où Click se déclenche, donc sa nouvelle valeur indique
        // l'intention de l'utilisateur (activer ou désactiver).
        private async void TogglePackageFilter_Click(object sender, RoutedEventArgs e)
        {
            if (TogglePackageFilter.IsChecked == true)
            {
                await ApplyPackageFilterAsync(TxtPackageFilter?.Text?.Trim());
                TogglePackageFilter.IsChecked = _packageFilterActive; // Corrige l'état si la résolution du PID a échoué
            }
            else
            {
                DeactivatePackageFilter();
            }
        }

        private async Task ApplyPackageFilterAsync(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return;

            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            TxtPackageFilter.ToolTip = $"{pkg} (recherche du PID...)";

            string pid = await _foregroundAppService.GetPidForPackageAsync(dev.Serial, pkg);

            if (!string.IsNullOrEmpty(pid))
            {
                _targetForegroundPid = pid;
                _packageFilterActive = true;
                TxtPackageFilter.ToolTip = $"Filtre actif : {pkg} (PID {pid})";
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🎯 Filtrage actif sur : {pkg} (PID {pid})");
            }
            else
            {
                _packageFilterActive = false;
                _targetForegroundPid = "";
                TxtPackageFilter.ToolTip = $"{pkg} : PID introuvable — l'app tourne-t-elle ?";
            }

            ApplyFiltersToExistingLogs();
        }

        private void DeactivatePackageFilter()
        {
            _packageFilterActive = false;
            _targetForegroundPid = "";
            TxtPackageFilter.ToolTip = "Nom de package exact (ex: com.zebra.launcher) — vide = toutes les applications";
            TogglePackageFilter.IsChecked = false;
            ApplyFiltersToExistingLogs();
        }

        private void BtnClearPackageFilter_Click(object sender, RoutedEventArgs e)
        {
            TxtPackageFilter.Clear();
            DeactivatePackageFilter();
        }

        // --- NAVIGATION VERS LA PROCHAINE ERREUR ---
        private void BtnNextError_Click(object sender, RoutedEventArgs e)
        {
            if (_displayLogs.Count == 0) return;

            int startIndex = LogsDataGrid.SelectedIndex;
            int searchStart = startIndex < 0 ? 0 : startIndex + 1;

            for (int i = searchStart; i < _displayLogs.Count; i++)
            {
                if (IsErrorLevel(_displayLogs[i])) { SelectAndScrollToLog(i); return; }
            }

            // Rien trouvé après la sélection : on boucle depuis le début jusqu'à la position de départ.
            for (int i = 0; i < searchStart && i < _displayLogs.Count; i++)
            {
                if (IsErrorLevel(_displayLogs[i])) { SelectAndScrollToLog(i); return; }
            }

            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⏭️ Aucune ligne Erreur/Fatal dans les logs affichés.");
        }

        private static bool IsErrorLevel(LogEntry log) => log.Level == "E" || log.Level == "F";

        private void SelectAndScrollToLog(int index)
        {
            if (RbViewTable?.IsChecked != true) { RbViewTable.IsChecked = true; }

            LogsDataGrid.SelectedIndex = index;
            LogsDataGrid.ScrollIntoView(_displayLogs[index]);
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (RbViewTable.IsChecked == true)
            {
                LogsDataGrid.Visibility = Visibility.Visible;
                LogsRawScroll.Visibility = Visibility.Collapsed;
            }
            else
            {
                LogsDataGrid.Visibility = Visibility.Collapsed;
                LogsRawScroll.Visibility = Visibility.Visible;
                ApplyFiltersToExistingLogs(); // Rafraîchit la zone texte
            }
        }

        // --- ACTIONS & EXPORTS ---
        private async void BtnClearLogDevice_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev != null && dev.State == "device")
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} logcat -c");
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 🧹 Tampon Logcat vidé sur le terminal (l'affichage actuel est conservé).");
            }
        }

        private void BtnClearLogUI_Click(object sender, RoutedEventArgs e)
        {
            _masterLogs.Clear();
            _displayLogs.Clear();
            TxtLogsRaw.Clear();
            TxtLogStats.Text = "Lignes affichées : 0 / 10000";
        }

        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            if (RbViewRaw.IsChecked == true)
            {
                // Vue brute : pas de notion de sélection ligne par ligne. On reconstruit depuis _displayLogs
                // en entier plutôt que de copier TxtLogsRaw.Text, qui n'affiche que les RAW_VIEW_MAX_LINES
                // dernières lignes à l'écran — le presse-papier, lui, doit contenir tout ce qui est filtré.
                StringBuilder sbRaw = new StringBuilder();
                foreach (var log in _displayLogs) sbRaw.AppendLine(log.RawLine);
                Clipboard.SetText(sbRaw.ToString());
            }
            else
            {
                var selected = LogsDataGrid.SelectedItems.Cast<LogEntry>().ToList();
                IEnumerable<LogEntry> source = selected.Count > 0 ? (IEnumerable<LogEntry>)selected : _displayLogs;

                StringBuilder sb = new StringBuilder();
                foreach (var log in source) sb.AppendLine(log.RawLine);
                Clipboard.SetText(sb.ToString());
            }
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📋 Logs copiés dans le presse-papier.");
        }

        private void BtnSaveLogs_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            string serial = dev != null ? dev.Serial : "Offline";

            string savePath = System.IO.Path.Combine(GetCaptureFolderPath(), $"Logcat_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            StringBuilder sb = new StringBuilder();
            foreach (var log in _displayLogs) sb.AppendLine(log.RawLine);

            try
            {
                System.IO.File.WriteAllText(savePath, sb.ToString());
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 💾 Logs sauvegardés dans : {savePath}");
                ShowInExplorer(savePath);
            }
            catch (Exception ex)
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ❌ Sauvegarde des logs impossible : {ex.Message}");
            }
        }

        // Un Bugreport complet prend couramment 2 à 5 minutes (davantage sur les terminaux lents) : le timeout
        // par défaut d'AdbManager (30 s) tuait systématiquement le process avant la fin.
        private static readonly TimeSpan BugReportTimeout = TimeSpan.FromMinutes(15);

        private async void BtnBugReport_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            bool confirm = await ShowCustomDialogAsync("☢️ GÉNÉRER BUGREPORT", "Le Bugreport capture toute la vie du système pour les constructeurs.\n\nAttention : Cette opération prend entre 2 et 5 minutes sur le terminal. Voulez-vous démarrer ?", "Oui, Lancer", "Annuler", true);
            if (!confirm) return;

            string savePath = System.IO.Path.Combine(GetCaptureFolderPath(), $"Bugreport_{FileNameHelper.Sanitize(dev.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.zip");
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ☢️ Lancement du Bugreport (Ceci peut prendre 5 minutes)...");
            BtnBugReport.IsEnabled = false;
            BtnBugReport.Content = "⏳ Génération...";

            try
            {
                // L'argument zip est essentiel pour les appareils récents. ExecuteAsync est déjà asynchrone :
                // pas besoin de Task.Run, et on reste sur le thread UI pour la suite (pas de Dispatcher.Invoke).
                var result = await _adbManager.ExecuteAsync($"-s {dev.Serial} bugreport {AdbManager.QuoteLocalArg(savePath)}", BugReportTimeout);

                if (result.IsSuccess && System.IO.File.Exists(savePath))
                {
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ✅ Bugreport terminé et sauvegardé : {savePath}");
                    ShowInExplorer(savePath);
                }
                else
                {
                    string detail = (result.StandardError ?? "").Trim();
                    if (string.IsNullOrEmpty(detail)) detail = (result.StandardOutput ?? "").Trim();
                    AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ❌ Échec du Bugreport (code {result.ExitCode}) : {detail}");
                }
            }
            catch (Exception ex)
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ❌ Erreur Bugreport : {ex.Message}");
            }
            finally
            {
                BtnBugReport.IsEnabled = true;
                BtnBugReport.Content = "☢️ GÉNÉRER BUGREPORT (.zip)";
            }
        }
    }
}
