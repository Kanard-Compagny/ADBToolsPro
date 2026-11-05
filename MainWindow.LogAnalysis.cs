using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace ADBToolsNew
{
    /// <summary>
    /// Analyse automatique des logs (page Logs) : sur la capture en direct, sur le journal actuel du terminal
    /// (logcat -d) ou sur un fichier reçu (logcat .txt, bugreport .zip). Le moteur est dans Core/LogAnalyzer.cs.
    /// </summary>
    public partial class MainWindow
    {
        private LogAnalysis _lastLogAnalysis;
        private string _lastLogAnalysisName = "";
        private static readonly TimeSpan QuickLogcatTimeout = TimeSpan.FromSeconds(90);

        private async void BtnAnalyzeLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_masterLogs.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("🩺 Analyse des logs"),
                    Loc.T("Aucun log à analyser pour l'instant.\n\nDémarrez la capture puis reproduisez le problème, ou utilisez « Analyse rapide du terminal » pour lire son journal actuel, ou « Ouvrir un fichier » pour un logcat ou un bugreport reçu."),
                    Loc.T("OK"), "", false);
                return;
            }
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            string source = ToggleLogcatRead.IsChecked == true && dev != null
                ? Loc.F("Capture en direct ({0})", dev.Serial)
                : Loc.T("Logs affichés");
            await RunLogAnalysisAsync(_masterLogs.ToList(), source, dev?.Serial ?? "");
        }

        private async void BtnQuickLogAnalysis_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("⚡ Analyse rapide"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            if (!await ConfirmReplaceLogsAsync()) return;

            SetLogAnalysisBusy(true, BtnQuickLogAnalysis, Loc.T("⏳ Lecture du journal…"));
            try
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚡ Lecture du journal du terminal (logcat -d)…", DateTime.Now));
                var res = await _adbManager.ExecuteAsync($"-s {dev.Serial} logcat -d -v threadtime", QuickLogcatTimeout);
                string output = res.StandardOutput ?? "";
                if (output.Trim().Length == 0)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Journal vide ou illisible : {1}", DateTime.Now, (res.StandardError ?? "").Trim()));
                    return;
                }
                var entries = await Task.Run(() => LogImport.ParseLines(output.Split('\n'), false));
                LoadLogsIntoView(entries);
                await RunLogAnalysisAsync(entries, Loc.F("Journal du terminal {0} (logcat -d)", dev.Serial), dev.Serial);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Analyse rapide impossible : {1}", DateTime.Now, ex.Message));
            }
            finally
            {
                SetLogAnalysisBusy(false, BtnQuickLogAnalysis, Loc.T("⚡ Analyse rapide du terminal"));
            }
        }

        private async void BtnOpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Ouvrir un fichier de logs"),
                Filter = Loc.T("Logs Android (*.txt;*.log;*.zip)|*.txt;*.log;*.zip|Tous les fichiers (*.*)|*.*"),
                InitialDirectory = Directory.Exists(GetCaptureFolderPath()) ? GetCaptureFolderPath() : "",
            };
            if (dlg.ShowDialog(this) != true) return;
            if (!await ConfirmReplaceLogsAsync()) return;

            string path = dlg.FileName;
            SetLogAnalysisBusy(true, BtnOpenLogFile, Loc.T("⏳ Lecture du fichier…"));
            try
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📂 Lecture de {1}…", DateTime.Now, Path.GetFileName(path)));
                var entries = await Task.Run(() => LogImport.LoadFile(path));
                if (entries.Count == 0)
                {
                    await ShowCustomDialogAsync(Loc.T("📂 Ouvrir un fichier"), Loc.T("Ce fichier ne contient pas de lignes de log Android reconnues."), Loc.T("OK"), "", false);
                    return;
                }
                LoadLogsIntoView(entries);
                await RunLogAnalysisAsync(entries, Loc.F("Fichier {0}", Path.GetFileName(path)), Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("📂 Ouvrir un fichier"), Loc.F("Lecture impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
            finally
            {
                SetLogAnalysisBusy(false, BtnOpenLogFile, Loc.T("📂 Ouvrir un fichier"));
            }
        }

        /// <summary>Le journal du terminal ou un fichier remplace l'affichage : la capture en direct est arrêtée.</summary>
        private async Task<bool> ConfirmReplaceLogsAsync()
        {
            if (_masterLogs.Count > 0)
            {
                bool ok = await ShowCustomDialogAsync(Loc.T("🩺 Analyse des logs"),
                    Loc.T("Les logs affichés vont être remplacés (pensez à les sauvegarder si besoin). Continuer ?"),
                    Loc.T("Remplacer"), Loc.T("Annuler"), false);
                if (!ok) return false;
            }
            if (ToggleLogcatRead.IsChecked == true)
            {
                ToggleLogcatRead.IsChecked = false;
                ToggleLogcatRead.Content = Loc.T("▶️ Démarrer");
                StopLogcat();
                AnimateNavButton(BtnNavLogs, false);
            }
            return true;
        }

        /// <summary>Affiche les entrées dans le tableau (les MAX_LOGS dernières : l'analyse, elle, porte sur tout).</summary>
        private void LoadLogsIntoView(List<LogEntry> entries)
        {
            _masterLogs.Clear();
            _masterLogs.AddRange(entries.Count > MAX_LOGS ? entries.Skip(entries.Count - MAX_LOGS) : entries);
            ApplyFiltersToExistingLogs();
            UpdateLogStatsText();
            if (entries.Count > MAX_LOGS)
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ℹ️ {1} lignes lues : les {2} dernières sont affichées, l'analyse porte sur toutes.", DateTime.Now, entries.Count, MAX_LOGS));
        }

        private async Task RunLogAnalysisAsync(List<LogEntry> entries, string source, string name)
        {
            var analysis = await Task.Run(() => LogAnalyzer.Analyze(entries, source));
            _lastLogAnalysis = analysis;
            _lastLogAnalysisName = name ?? "";

            TxtLogAnalysisSource.Text = source;
            TxtLogAnalysisPeriod.Text = analysis.PeriodText;
            TxtLogAnalysisVerdict.Text = analysis.Verdict;
            TxtLogAnalysisBad.Text = Loc.F("❌ {0} important(s)", analysis.BadCount);
            TxtLogAnalysisWarn.Text = Loc.F("⚠️ {0} à surveiller", analysis.WarnCount);
            TxtLogAnalysisInfo.Text = Loc.F("ℹ️ {0} info(s)", analysis.InfoCount);
            LogFindingsList.ItemsSource = analysis.Findings;
            LogAnalysisPanel.Visibility = Visibility.Visible;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🩺 Analyse des logs : {1}", DateTime.Now, analysis.Verdict));
        }

        private void SetLogAnalysisBusy(bool busy, System.Windows.Controls.Button button, string text)
        {
            BtnAnalyzeLogs.IsEnabled = BtnQuickLogAnalysis.IsEnabled = BtnOpenLogFile.IsEnabled = !busy;
            button.Content = text;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
        }

        private void BtnCloseLogAnalysis_Click(object sender, RoutedEventArgs e)
        {
            LogAnalysisPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>Un clic sur un problème sélectionne sa première ligne dans le tableau (filtres retirés si besoin).</summary>
        private void LogFinding_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is LogFinding f) || f.FirstEntry == null) return;
            int index = _displayLogs.IndexOf(f.FirstEntry);
            if (index < 0 && _masterLogs.Contains(f.FirstEntry))
            {
                // La ligne est masquée par un filtre : on revient à « Tous » sans recherche ni cible.
                TxtLogSearch.Text = "";
                if (_packageFilterActive) DeactivatePackageFilter();
                RbLogAll.IsChecked = true;
                ApplyFiltersToExistingLogs();
                index = _displayLogs.IndexOf(f.FirstEntry);
            }
            if (index >= 0)
            {
                ChkAutoScroll.IsChecked = false; // sinon la capture en direct repartirait aussitôt en bas
                SelectAndScrollToLog(index);
            }
            else
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ℹ️ Cette ligne ne fait plus partie des logs affichés : voir « Voir les lignes » ou le rapport exporté.", DateTime.Now));
            }
        }

        private void BtnCopyLogAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_lastLogAnalysis == null) return;
            try
            {
                Clipboard.SetText(_lastLogAnalysis.ToText());
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📋 Rapport d'analyse copié dans le presse-papier.", DateTime.Now));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Copie impossible : {1}", DateTime.Now, ex.Message));
            }
        }

        private void BtnExportLogAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (_lastLogAnalysis == null) return;
            string name = string.IsNullOrWhiteSpace(_lastLogAnalysisName) ? "Logs" : FileNameHelper.Sanitize(_lastLogAnalysisName);
            string path = Path.Combine(GetCaptureFolderPath(), $"AnalyseLogs_{name}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            try
            {
                File.WriteAllText(path, _lastLogAnalysis.ToText(), new System.Text.UTF8Encoding(true));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Rapport d'analyse enregistré : {1}", DateTime.Now, path));
                ShowInExplorer(path);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Enregistrement impossible : {1}", DateTime.Now, ex.Message));
            }
        }
    }
}
