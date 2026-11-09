using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Analyse de fichiers — outils d'enquête : avant l'incident, vie de l'appli métier, sessions et redémarrages,
    /// signets du technicien, pannes connues de l'équipe (signatures). Moteur : Core/LogInsights.cs.
    /// </summary>
    public partial class MainWindow
    {
        private List<LogInsights.Signature> _faSignatures = new List<LogInsights.Signature>();
        private List<LogInsights.Session> _faSessions = new List<LogInsights.Session>();
        private List<LogInsights.Bookmark> _faBookmarks = new List<LogInsights.Bookmark>();
        private LogInsights.TrendIndex _faTrends;
        private List<LogInsights.BeforeRow> _faBefore;
        private string _faBeforeLabel = "";
        private LogInsights.AppLife _faAppLife;
        private FileLogEntry _faPendingLine;
        private DispatcherTimer _faSigTestTimer;

        // ------------------------------------------------------------------ Chargement

        private void LoadFaInsights(LogArchive a)
        {
            _faTrends = null;
            _faBefore = null; _faBeforeLabel = "";
            FaBeforeList.ItemsSource = null;
            TxtFaBeforeSummary.Text = Loc.T("« Aller à l'incident » ou « 🕵 Avant cette ligne » : ce qui change dans les minutes qui précèdent (messages jamais vus avant, pics de fréquence, événements marquants).");

            _faSignatures = LogInsights.LoadSignatures(TeamSignatureFile, LocalSignatureFile);
            RefreshFaSignaturesPanel();

            _faSessions = LogInsights.Sessions(a);
            FaSessionsList.ItemsSource = _faSessions;
            int unexpected = _faSessions.Count(s => s.Unexpected);
            TxtFaSessionsSummary.Text = _faSessions.Count <= 1 && unexpected == 0
                ? Loc.T("Aucun redémarrage dans ce relevé.")
                : Loc.F("{0} session(s), donc {1} redémarrage(s), dont {2} inattendu(s). Cliquez pour aller au début d'une session.", _faSessions.Count, Math.Max(0, _faSessions.Count - 1), unexpected);

            _faBookmarks = LogInsights.LoadBookmarks(LogInsights.BookmarkFile(AppSettings.DataFolder, a.Path), a);
            FaBookmarksList.ItemsSource = _faBookmarks.ToList();

            // Appli métier : celle suivie par l'enregistreur si c'en est un, sinon la plus démarrée hors système.
            var apps = LogInsights.StartedApps(a);
            CmbFaApp.ItemsSource = apps.Select(p => p.Key).ToList();
            string followed = a.Entries.Where(e => e.Tag == "ADBTools-Rec").Select(e => Regex.Match(e.Message ?? "", @"application suivie ([\w.]+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            string guess = followed ?? apps.Select(p => p.Key).FirstOrDefault(k => !IsSystemPackage(k));
            _faAppLife = null;
            FaAppList.ItemsSource = null;
            TxtFaAppSummary.Text = apps.Count == 0 ? Loc.T("Aucun démarrage d'application dans ce relevé.") : "";
            if (!string.IsNullOrEmpty(guess)) { CmbFaApp.Text = guess; ShowFaAppLife(guess); }
        }

        private static bool IsSystemPackage(string p) =>
            p.StartsWith("android", StringComparison.Ordinal) || p.StartsWith("com.android.", StringComparison.Ordinal) || p.StartsWith("com.google.", StringComparison.Ordinal)
            || p.StartsWith("com.qualcomm", StringComparison.Ordinal) || p.StartsWith("com.qti", StringComparison.Ordinal) || p.StartsWith("vendor.", StringComparison.Ordinal)
            || p.StartsWith("com.symbol.", StringComparison.Ordinal) || p.StartsWith("com.zebra.", StringComparison.Ordinal) || p.StartsWith("com.honeywell.", StringComparison.Ordinal)
            || p.StartsWith("com.samsung.", StringComparison.Ordinal) || p.StartsWith("com.sec.", StringComparison.Ordinal) || p == "system"
            || p.StartsWith(".", StringComparison.Ordinal) || p.Count(ch => ch == '.') < 2;

        /// <summary>Analyse complète d'une partie du relevé : analyseur, pannes connues, ANR / tombstones, redémarrages inattendus, tendances.</summary>
        private static LogAnalysis AnalyzeFa(LogArchive a, IList<FileLogEntry> lines, int from, int to, List<LogInsights.Signature> sigs,
                                             LogInsights.TrendIndex trends, List<LogInsights.Session> sessions)
        {
            var an = LogAnalyzer.Analyze(lines.Cast<LogEntry>().ToList(), a.Title);
            var extra = new List<LogFinding>();
            extra.AddRange(LogInsights.MatchSignatures(lines, sigs));
            extra.AddRange(LogInsights.AnnexFindings(a, from, to));
            extra.AddRange(LogInsights.SessionFindings(sessions, from, to));
            var all = LogInsights.FoldBootNoise(extra.Concat(an.Findings).ToList(), sessions)
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Category == LogInsights.CatKnown ? 0 : f.Category == LogAnalyzer.CatIncident ? 1 : 2)
                .ToList();
            an.Findings.Clear();
            an.Findings.AddRange(all);
            LogInsights.ApplyTrends(an.Findings, trends);
            return an;
        }

        // ------------------------------------------------------------------ Avant l'incident

        private async void BtnFaBefore_Click(object sender, RoutedEventArgs e)
        {
            if (FaGrid.SelectedItem is FileLogEntry sel && sel.Minute >= 0)
                await RunFaBeforeAsync(sel.Minute, sel.Time);
        }

        private async Task RunFaBeforeAsync(int minute, string label)
        {
            if (_faArchive == null) return;
            var a = _faArchive;
            int window = Math.Max(5, FaWindowMinutes);
            var sources = a.LogFiles.Any(f => !f.Included) ? new HashSet<int>(a.LogFiles.Where(f => f.Included).Select(f => f.SourceId)) : null;
            RbFaBefore.IsChecked = true;
            TxtFaBeforeSummary.Text = Loc.T("⏳ Recherche de ce qui change avant ce moment…");
            var rows = await Task.Run(() => LogInsights.BeforeIncident(a, minute, window, sources));
            if (a != _faArchive) return;
            _faBefore = rows;
            _faBeforeLabel = Loc.F("Les {0} minutes avant {1}", window, LogFinding.ShortTime(label));
            FaBeforeList.ItemsSource = rows;
            int nNew = rows.Count(r => r.Kind == "new"), nSpike = rows.Count(r => r.Kind == "spike"), nEv = rows.Count(r => r.Kind == "event");
            TxtFaBeforeSummary.Text = _faBeforeLabel + " : " + (rows.Count == 0
                ? Loc.T("rien de nouveau ni d'inhabituel par rapport aux heures précédentes.")
                : Loc.F("{0} événement(s) marquant(s), {1} message(s) jamais vu(s) avant, {2} en forte hausse. Cliquez pour voir la ligne.", nEv, nNew, nSpike));
        }

        /// <summary>Clic sur une ligne des panneaux d'enquête : la ligne correspondante dans le tableau.</summary>
        private void FaInsightList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            object item = (sender as ListBox)?.SelectedItem;
            FileLogEntry entry = null;
            if (item is LogInsights.BeforeRow br) entry = br.Entry;
            else if (item is LogInsights.AppEvent ae) entry = ae.Entry;
            else if (item is LogInsights.Session se) entry = se.StartEntry;
            else if (item is LogInsights.Bookmark bm) entry = bm.Entry;
            if (entry != null) FaGoToEntry(entry);
        }

        // ------------------------------------------------------------------ Vie de l'appli métier

        private void CmbFaApp_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFaApp.SelectedItem is string pkg) ShowFaAppLife(pkg);
        }

        private void CmbFaApp_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(CmbFaApp.Text)) ShowFaAppLife(CmbFaApp.Text.Trim());
        }

        private async void ShowFaAppLife(string pkg)
        {
            if (_faArchive == null || string.IsNullOrEmpty(pkg)) return;
            var a = _faArchive;
            TxtFaAppSummary.Text = Loc.T("⏳ Lecture…");
            var life = await Task.Run(() => LogInsights.Lifecycle(a, pkg));
            if (a != _faArchive) return;
            _faAppLife = life;
            FaAppList.ItemsSource = life.Events;
            TxtFaAppSummary.Text = life.Events.Count == 0 ? Loc.F("Aucun événement pour {0} dans ce relevé.", pkg) : life.Summary;
        }

        // ------------------------------------------------------------------ Signets

        private void BtnFaBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaGrid.SelectedItem is FileLogEntry sel)) return;
            _faPendingLine = sel;
            TxtFaBookmarkLine.Text = $"{sel.Time}  {sel.Tag}: {sel.Message}";
            TxtFaBookmarkNote.Text = "";
            FaBookmarkOverlay.Visibility = Visibility.Visible;
            TxtFaBookmarkNote.Focus();
        }

        private void BtnFaBookmarkCancel_Click(object sender, RoutedEventArgs e) => FaBookmarkOverlay.Visibility = Visibility.Collapsed;

        private void BtnFaBookmarkOk_Click(object sender, RoutedEventArgs e)
        {
            FaBookmarkOverlay.Visibility = Visibility.Collapsed;
            var sel = _faPendingLine;
            if (sel == null || _faArchive == null) return;
            _faBookmarks.RemoveAll(b => b.Entry == sel);
            _faBookmarks.Add(new LogInsights.Bookmark
            {
                Time = sel.Time, Tag = sel.Tag, Message = (sel.Message ?? "").Length > 200 ? sel.Message.Substring(0, 200) : sel.Message ?? "",
                Note = (TxtFaBookmarkNote.Text ?? "").Trim(), Index = sel.Index, Entry = sel,
            });
            _faBookmarks = _faBookmarks.OrderBy(b => b.Index).ToList();
            SaveFaBookmarks();
        }

        private void BtnFaDeleteBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaBookmarksList.SelectedItem is LogInsights.Bookmark bm)) return;
            _faBookmarks.Remove(bm);
            SaveFaBookmarks();
        }

        private void SaveFaBookmarks()
        {
            FaBookmarksList.ItemsSource = _faBookmarks.ToList();
            DrawFaTimeline();
            try { LogInsights.SaveBookmarks(LogInsights.BookmarkFile(AppSettings.DataFolder, _faArchive.Path), _faBookmarks); }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Signets non enregistrés : {1}", DateTime.Now, ex.Message)); }
        }

        // ------------------------------------------------------------------ Pannes connues (signatures)

        /// <summary>Fichier partagé de l'équipe (dans le dossier de diffusion des mises à jour), s'il est accessible.</summary>
        private static string TeamSignatureFile
        {
            get
            {
                string share = AppSettings.Current.UpdateShareFolder;
                try { return !string.IsNullOrWhiteSpace(share) && Directory.Exists(share) ? Path.Combine(share, "ADBTools_pannes_connues.json") : null; }
                catch { return null; }
            }
        }

        private static string LocalSignatureFile => Path.Combine(AppSettings.DataFolder, "pannes_connues.json");

        private void RefreshFaSignaturesPanel()
        {
            FaSignaturesList.ItemsSource = _faSignatures.ToList();
            string team = TeamSignatureFile;
            TxtFaSignaturesInfo.Text = (team != null
                ? Loc.F("Partagées avec l'équipe : {0}", team)
                : Loc.F("Gardées sur ce poste ({0}). Renseignez le dossier de diffusion de l'équipe (Paramètres ▸ Mises à jour) pour les partager.", LocalSignatureFile))
                + " " + Loc.F("{0} panne(s) connue(s), reconnues automatiquement dans chaque relevé.", _faSignatures.Count);
        }

        private void BtnFaSignature_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaGrid.SelectedItem is FileLogEntry sel)) return;
            _faPendingLine = sel;
            TxtFaSigTitle.Text = "";
            TxtFaSigTag.Text = sel.Tag ?? "";
            TxtFaSigPattern.Text = LogInsights.SuggestPattern(sel.Message);
            TxtFaSigDiagnosis.Text = "";
            TxtFaSigSolution.Text = "";
            CmbFaSigSeverity.SelectedIndex = 0;
            string team = TeamSignatureFile;
            TxtFaSignatureWhere.Text = team != null ? Loc.F("Elle sera partagée avec l'équipe ({0}) et reconnue automatiquement dans tous les relevés.", team)
                                                    : Loc.T("Elle sera gardée sur ce poste et reconnue automatiquement dans tous les relevés.");
            FaSignatureOverlay.Visibility = Visibility.Visible;
            TxtFaSigTitle.Focus();
        }

        private void BtnFaSignatureCancel_Click(object sender, RoutedEventArgs e) => FaSignatureOverlay.Visibility = Visibility.Collapsed;

        private void TxtFaSigPattern_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_faSigTestTimer == null)
            {
                _faSigTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _faSigTestTimer.Tick += async (s, a) => { _faSigTestTimer.Stop(); await TestFaSignatureAsync(); };
            }
            _faSigTestTimer.Stop();
            _faSigTestTimer.Start();
        }

        /// <summary>Combien de lignes du relevé ouvert le motif reconnaît (pour vérifier avant d'enregistrer).</summary>
        private async Task TestFaSignatureAsync()
        {
            if (_faArchive == null || FaSignatureOverlay.Visibility != Visibility.Visible) return;
            var sig = new LogInsights.Signature { Pattern = TxtFaSigPattern.Text ?? "", Tag = (TxtFaSigTag.Text ?? "").Trim() };
            try { new Regex(sig.Pattern); }
            catch (ArgumentException) { TxtFaSigTest.Text = Loc.T("⚠️ Expression régulière invalide"); return; }
            var a = _faArchive;
            var found = await Task.Run(() => LogInsights.MatchSignatures(a.Entries, new List<LogInsights.Signature> { sig }));
            TxtFaSigTest.Text = found.Count == 0 ? Loc.T("⚠️ Aucune ligne de ce relevé ne correspond.")
                : Loc.F("✓ {0} ligne(s) de ce relevé correspondent.", found[0].Count);
        }

        private async void BtnFaSignatureOk_Click(object sender, RoutedEventArgs e)
        {
            string title = (TxtFaSigTitle.Text ?? "").Trim(), pattern = (TxtFaSigPattern.Text ?? "").Trim();
            if (title.Length == 0 || pattern.Length == 0) { TxtFaSigTest.Text = Loc.T("⚠️ Donnez au moins un nom et un message à reconnaître."); return; }
            try { new Regex(pattern); }
            catch (ArgumentException) { TxtFaSigTest.Text = Loc.T("⚠️ Expression régulière invalide"); return; }
            FaSignatureOverlay.Visibility = Visibility.Collapsed;

            var sig = new LogInsights.Signature
            {
                Title = title, Tag = (TxtFaSigTag.Text ?? "").Trim(), Pattern = pattern,
                Diagnosis = (TxtFaSigDiagnosis.Text ?? "").Trim(), Solution = (TxtFaSigSolution.Text ?? "").Trim(),
                Severity = (CmbFaSigSeverity.SelectedItem as ComboBoxItem)?.Tag as string ?? "bad",
                Author = AppSettings.Current.ReportTechnician ?? "",
            };
            string file = TeamSignatureFile ?? LocalSignatureFile;
            try
            {
                var existing = LogInsights.LoadSignatures(file);
                existing.Add(sig);
                LogInsights.SaveSignatures(file, existing);
            }
            catch (Exception ex)
            {
                // Dossier de l'équipe en lecture seule ou déconnecté : on garde la panne sur ce poste.
                file = LocalSignatureFile;
                var existing = LogInsights.LoadSignatures(file);
                existing.Add(sig);
                LogInsights.SaveSignatures(file, existing);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Dossier de l'équipe inaccessible ({1}) : panne connue gardée sur ce poste.", DateTime.Now, ex.Message));
            }
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⭐ Panne connue enregistrée : {1} ({2})", DateTime.Now, title, file));
            await ReloadFaSignaturesAsync();
        }

        private async void BtnFaDeleteSignature_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaSignaturesList.SelectedItem is LogInsights.Signature sig) || string.IsNullOrEmpty(sig.Source)) return;
            bool ok = await ShowCustomDialogAsync(Loc.T("⭐ Pannes connues"), Loc.F("Supprimer la panne connue « {0} » ?{1}", sig.Title,
                sig.Source == TeamSignatureFile ? "\n\n" + Loc.T("Elle sera retirée pour toute l'équipe.") : ""), Loc.T("Supprimer"), Loc.T("Annuler"), false);
            if (!ok) return;
            try
            {
                var list = LogInsights.LoadSignatures(sig.Source);
                list.RemoveAll(s => s.Id == sig.Id);
                LogInsights.SaveSignatures(sig.Source, list);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("⭐ Pannes connues"), Loc.F("Suppression impossible : {0}", ex.Message), Loc.T("OK"), "", false);
                return;
            }
            await ReloadFaSignaturesAsync();
        }

        /// <summary>Pannes connues relues, puis constats recalculés pour qu'elles apparaissent tout de suite.</summary>
        private async Task ReloadFaSignaturesAsync()
        {
            _faSignatures = LogInsights.LoadSignatures(TeamSignatureFile, LocalSignatureFile);
            RefreshFaSignaturesPanel();
            if (_faArchive == null) return;
            var a = _faArchive; var sigs = _faSignatures; var trends = _faTrends; var sessions = _faSessions;
            _faFullAnalysis = await Task.Run(() => AnalyzeFa(a, a.Entries, int.MinValue, int.MaxValue, sigs, trends, sessions));
            await RefreshFaFindingsAsync(force: true);
        }
    }
}
