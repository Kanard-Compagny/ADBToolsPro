using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

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
        private string _lastLogAnalysisSignature = "";
        private bool _logAnalysisRunning;
        private bool _logAnalysisClosedByUser;
        private LogEntry _lastAnalyzedTail;
        private DispatcherTimer _autoAnalysisTimer;
        private static readonly TimeSpan AutoAnalysisInterval = TimeSpan.FromSeconds(5);

        // ------------------------------------------------------------------ Analyse en continu

        /// <summary>Appelé au démarrage de la capture en direct : l'analyse se met à jour toute seule.</summary>
        private void StartAutoLogAnalysis()
        {
            StopAutoLogAnalysis();
            _logAnalysisClosedByUser = false;
            _lastAnalyzedTail = null;
            if (ChkAutoAnalyze?.IsChecked != true) return;
            _autoAnalysisTimer = new DispatcherTimer { Interval = AutoAnalysisInterval };
            _autoAnalysisTimer.Tick += async (s, ev) => await AutoAnalyzeTickAsync();
            _autoAnalysisTimer.Start();
        }

        private void StopAutoLogAnalysis()
        {
            if (_autoAnalysisTimer == null) return;
            _autoAnalysisTimer.Stop();
            _autoAnalysisTimer = null;
        }

        /// <summary>Nouvelle analyse seulement si de nouvelles lignes sont arrivées depuis la précédente.</summary>
        private async Task AutoAnalyzeTickAsync()
        {
            if (_logAnalysisRunning || _masterLogs.Count == 0) return;
            var tail = _masterLogs[_masterLogs.Count - 1];
            if (ReferenceEquals(tail, _lastAnalyzedTail)) return;
            _lastAnalyzedTail = tail;
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            await RunLogAnalysisAsync(_masterLogs.ToList(), Loc.F("Capture en direct ({0})", dev?.Serial ?? "?"), dev?.Serial ?? "", silent: true);
        }

        private void ChkAutoAnalyze_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (ChkAutoAnalyze.IsChecked == true && _logcatService.IsRunning) StartAutoLogAnalysis();
            else StopAutoLogAnalysis();
        }

        /// <summary>Fin de la capture : dernière analyse, pour que le résultat corresponde à tout ce qui a été capturé.</summary>
        private async void FinishAutoLogAnalysis()
        {
            StopAutoLogAnalysis();
            if (ChkAutoAnalyze?.IsChecked == true && _masterLogs.Count > 0)
                await AutoAnalyzeTickAsync();
        }
        private static readonly TimeSpan QuickLogcatTimeout = TimeSpan.FromSeconds(90);

        private async void BtnAnalyzeLogs_Click(object sender, RoutedEventArgs e)
        {
            if (_masterLogs.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("🔎 Analyse des logs"),
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
                if (!string.IsNullOrEmpty(LogImport.LastImportNote))
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 {1}", DateTime.Now, LogImport.LastImportNote));
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
                bool ok = await ShowCustomDialogAsync(Loc.T("🔎 Analyse des logs"),
                    Loc.T("Les logs affichés vont être remplacés (pensez à les sauvegarder si besoin). Continuer ?"),
                    Loc.T("Remplacer"), Loc.T("Annuler"), false);
                if (!ok) return false;
            }
            if (ToggleLogcatRead.IsChecked == true)
            {
                ToggleLogcatRead.IsChecked = false;
                ToggleLogcatRead.Content = Loc.T("▶️ Démarrer");
                StopLogcat();
                StopAutoLogAnalysis();
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

        private async Task RunLogAnalysisAsync(List<LogEntry> entries, string source, string name, bool silent = false)
        {
            if (_logAnalysisRunning) return;
            _logAnalysisRunning = true;
            LogAnalysis analysis;
            try { analysis = await Task.Run(() => LogAnalyzer.Analyze(entries, source)); }
            finally { _logAnalysisRunning = false; }
            _lastLogAnalysis = analysis;
            _lastLogAnalysisName = name ?? "";
            if (!silent) _logAnalysisClosedByUser = false;

            TxtLogAnalysisSource.Text = source;
            TxtLogAnalysisPeriod.Text = analysis.PeriodText;
            TxtLogAnalysisVerdict.Text = analysis.Verdict;
            TxtLogAnalysisBad.Text = Loc.F("❌ {0} important(s)", analysis.BadCount);
            TxtLogAnalysisWarn.Text = Loc.F("⚠️ {0} à surveiller", analysis.WarnCount);
            TxtLogAnalysisInfo.Text = Loc.F("ℹ️ {0} info(s)", analysis.InfoCount);
            RenderLogFindings(keepScroll: silent, force: !silent);
            if (!_logAnalysisClosedByUser) LogAnalysisPanel.Visibility = Visibility.Visible;
            if (!silent) AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔎 Analyse des logs : {1}", DateTime.Now, analysis.Verdict));
        }

        /// <summary>Application ciblée dans la page Logs (champ « Cible » activé), sinon null.</summary>
        private string CurrentLogTarget() =>
            _packageFilterActive && !string.IsNullOrWhiteSpace(TxtPackageFilter?.Text) ? TxtPackageFilter.Text.Trim() : null;

        /// <summary>
        /// Affiche les problèmes : ceux de l'application ciblée en tête (🎯) ; option pour masquer les plantages
        /// des AUTRES applications. Réseau, Wi-Fi, système et lecteur ne sont jamais masqués : ce sont des
        /// ressources partagées, donc des causes possibles pour l'application ciblée.
        /// </summary>
        private void RenderLogFindings(bool keepScroll, bool force)
        {
            if (_lastLogAnalysis == null || LogFindingsList == null) return;
            string target = CurrentLogTarget();
            foreach (var f in _lastLogAnalysis.Findings) f.IsTarget = target != null && f.ConcernsApp(target, _targetForegroundPid);

            bool hideOthers = target != null && ChkLogOnlyTarget.IsChecked == true;
            var view = _lastLogAnalysis.Findings
                .Where(f => !(hideOthers && f.Category == LogAnalyzer.CatCrash && !f.IsTarget))
                .OrderBy(f => f.IsTarget ? 0 : 1)   // tri stable : l'ordre de gravité est conservé dans chaque groupe
                .ToList();
            int hidden = _lastLogAnalysis.Findings.Count - view.Count;

            ChkLogOnlyTarget.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
            if (target != null)
            {
                int mine = view.Count(f => f.IsTarget);
                TxtLogAnalysisTarget.Text = Loc.F("🎯 Cible : {0} — {1} problème(s) la concernent directement", target, mine)
                                            + (hidden > 0 ? "\n" + Loc.F("{0} plantage(s) d'autres applications masqué(s)", hidden) : "");
                TxtLogAnalysisTarget.Visibility = Visibility.Visible;
            }
            else TxtLogAnalysisTarget.Visibility = Visibility.Collapsed;

            // Liste remplacée seulement si elle a changé, en gardant la position de défilement : pendant la
            // capture en direct, le panneau ne doit pas « sauter » toutes les 5 secondes.
            string signature = (target ?? "") + "#" + hideOthers + "#" + string.Join("|", view.Select(f => f.Key + ":" + f.Count + ":" + (int)f.Severity));
            if (!force && signature == _lastLogAnalysisSignature) return;
            double offset = LogFindingsScroll.VerticalOffset;
            LogFindingsList.ItemsSource = view;
            _lastLogAnalysisSignature = signature;
            if (keepScroll) Dispatcher.BeginInvoke(new Action(() => LogFindingsScroll.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
            else LogFindingsScroll.ScrollToTop();
        }

        private void ChkLogOnlyTarget_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded) RenderLogFindings(keepScroll: false, force: true);
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
            _logAnalysisClosedByUser = true; // l'analyse en continu ne le rouvre pas ; « Analyser maintenant » oui
        }

        /// <summary>Un clic sur un problème sélectionne sa première ligne dans le tableau (filtres retirés si besoin).</summary>
        private async void LogFinding_Click(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is LogFinding f) || f.FirstEntry == null) return;
            int index = _displayLogs.IndexOf(f.FirstEntry);
            if (index < 0 && _masterLogs.Contains(f.FirstEntry))
            {
                // La ligne est masquée par un filtre (cible, niveau, recherche) : on demande avant de les retirer.
                string target = CurrentLogTarget();
                bool ok = await ShowCustomDialogAsync(Loc.T("🔎 Analyse des logs"),
                    target != null
                        ? Loc.F("Cette ligne ne concerne pas l'application ciblée ({0}) : elle est masquée par les filtres.\n\nRetirer les filtres (cible comprise) pour l'afficher ?", target)
                        : Loc.T("Cette ligne est masquée par les filtres (niveau ou recherche).\n\nRetirer les filtres pour l'afficher ?"),
                    Loc.T("Afficher la ligne"), Loc.T("Annuler"), false);
                if (!ok) return;
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
