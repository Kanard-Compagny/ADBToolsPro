using ADBToolsNew.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Page « Analyse de fichiers » : relevé complet reçu du terrain (archive RxLogger, bugreport, dossier, logcat),
    /// tous journaux fusionnés, recherche au fil de la frappe, filtres, « aller à l'incident », frise, constats de
    /// l'analyse automatique, fichiers annexes (ANR, tombstones, snapshots), fiche terminal et comparaison de deux
    /// relevés. Moteur : Core/LogArchive.cs.
    /// </summary>
    public partial class MainWindow
    {
        private const int FileAnalysisTabIndex = 11;
        private LogArchive _faArchive, _faCompare;
        private List<FileLogEntry> _faView = new List<FileLogEntry>();
        private List<LogArchive.TimelineBucket> _faBuckets = new List<LogArchive.TimelineBucket>();
        private CancellationTokenSource _faFilterCts;
        private DispatcherTimer _faDebounce;
        private int _faFrom = int.MinValue, _faTo = int.MaxValue;
        private int _faScrollToMinute = -1;
        private FileLogEntry _faScrollToEntry;
        private bool _faBusy, _faSuspendFilter;
        private ArchiveFile _faOpenFile;
        private LogAnalysis _faFullAnalysis, _faShownAnalysis;
        private List<LogCompareRow> _faCompareRows;
        private string _faCompareText = "", _faShownScope = "";
        private DispatcherTimer _faZoneTimer;
        private CancellationTokenSource _faZoneCts;
        private int _faZoneFrom = int.MinValue, _faZoneTo = int.MinValue;
        private int _faVisibleFrom = -1, _faVisibleTo = -1;
        private GridLength _faLeftWidth = new GridLength(250), _faRightWidth = new GridLength(400);
        private Rectangle _faHereRect;
        private Polygon _faHereMark;

        private void NavFileAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl == null) return;
            MainTabControl.SelectedIndex = FileAnalysisTabIndex;
            UpdateNavButtonsUI(BtnNavFileAnalysis);
        }

        // ------------------------------------------------------------------ Ouverture

        private async void BtnFaOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = Loc.T("Ouvrir un relevé de logs"),
                Filter = Loc.T("Relevés de logs (*.zip;*.txt;*.log)|*.zip;*.txt;*.log|Tous les fichiers (*.*)|*.*"),
            };
            if (dlg.ShowDialog(this) != true) return;
            await LoadFileAnalysisAsync(dlg.FileName);
        }

        private async void BtnFaOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string path = PickFolder(Loc.T("Dossier du relevé (RxLogger décompressé, bugreport…)"));
            if (path != null) await LoadFileAnalysisAsync(path);
        }

        private static string PickFolder(string description)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = description, ShowNewFolderButton = false })
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }

        /// <summary>Ouvre un relevé dans la page (appelé aussi à la récupération d'un enregistrement du terminal).</summary>
        internal async Task LoadFileAnalysisAsync(string path)
        {
            if (_faBusy) return;
            SetFaBusy(true);
            var progress = new Progress<string>(s => TxtFaSummary.Text = s);
            try
            {
                var archive = await Task.Run(() => LogArchive.Open(path, progress));
                if (archive.Entries.Count == 0 && !archive.Files.Any())
                {
                    await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.T("Aucun journal ni fichier exploitable dans ce relevé."), Loc.T("OK"), "", false);
                    return;
                }
                _faArchive = archive;
                _faCompare = null;
                _faCompareRows = null; _faCompareText = "";
                _faFullAnalysis = null; _faShownAnalysis = null;
                _faZoneFrom = _faZoneTo = int.MinValue;
                _faVisibleFrom = _faVisibleTo = -1;
                LoadFaInsights(archive);
                FaCompareGrid.ItemsSource = null;
                TxtFaCompareSummary.Text = Loc.T("« Comparer avec… » ouvre un second relevé (B) et liste les erreurs et avertissements propres à l'un des deux, ou bien plus fréquents (par heure). A = relevé ouvert.");

                FaLogFilesList.ItemsSource = archive.LogFiles.ToList();
                FaOtherFilesList.ItemsSource = archive.OtherFiles.ToList();
                FaDeviceList.ItemsSource = archive.Device;
                TxtFaDeviceEmpty.Visibility = archive.Device.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TxtFaFileContent.Text = "";
                TxtFaFileTitle.Text = Loc.T("Choisissez un fichier dans « Autres fichiers ».");
                _faOpenFile = null;

                TxtFaSummary.Text = Loc.F("{0} — {1} lignes de {2} journal(aux), du {3} au {4}.", archive.Title,
                    archive.Entries.Count.ToString("N0", CultureInfo.CurrentCulture), archive.LogFiles.Count(),
                    LogArchive.MinuteText(archive.SpanFrom), LogArchive.MinuteText(archive.SpanTo))
                    + (archive.Note.Length > 0 ? " " + archive.Note : "");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗂️ Relevé ouvert : {1}", DateTime.Now, TxtFaSummary.Text));
                BtnFaCompare.IsEnabled = archive.Entries.Count > 0;
                BtnFaExport.IsEnabled = archive.Entries.Count > 0;

                _faSuspendFilter = true;
                ResetFaFilterControls();
                _faSuspendFilter = false;
                _faFrom = int.MinValue; _faTo = int.MaxValue;
                await ApplyFaFilterAsync();

                // Analyse automatique de tout le relevé (gardée pour « De tout le relevé ») ; la zone regardée suit.
                TxtFaFindingsSummary.Text = Loc.T("⏳ Analyse en cours…");
                FaFindingsList.ItemsSource = null;
                var sigs = _faSignatures; var sessions = _faSessions;
                var analysis = await Task.Run(() =>
                {
                    var trends = LogInsights.BuildTrends(archive);
                    _faTrends = trends;
                    return AnalyzeFa(archive, archive.Entries, int.MinValue, int.MaxValue, sigs, trends, sessions);
                });
                if (_faArchive != archive) return;
                _faFullAnalysis = analysis;
                RbFaFindings.IsChecked = true;
                await RefreshFaFindingsAsync(force: true);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.F("Lecture impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
            finally
            {
                SetFaBusy(false);
            }
        }

        private void SetFaBusy(bool busy)
        {
            _faBusy = busy;
            FaProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BtnFaOpenFile.IsEnabled = !busy;
            BtnFaOpenFolder.IsEnabled = !busy;
            if (busy) { BtnFaCompare.IsEnabled = false; BtnFaExport.IsEnabled = false; }
            else if (_faArchive != null) { BtnFaCompare.IsEnabled = _faArchive.Entries.Count > 0; BtnFaExport.IsEnabled = _faArchive.Entries.Count > 0; }
        }

        private IEnumerable<ToggleButton> FaLevelToggles => new[] { TglFaF, TglFaE, TglFaW, TglFaI, TglFaD, TglFaV };

        private void ResetFaFilterControls()
        {
            TxtFaSearch.Text = "";
            TglFaRegex.IsChecked = false;
            foreach (var t in FaLevelToggles) t.IsChecked = true;
            TxtFaTag.Text = "";
            TxtFaPid.Text = "";
            TxtFaIncident.Text = "";
        }

        // ------------------------------------------------------------------ Filtres

        private void FaFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (_faSuspendFilter || _faArchive == null) return;
            if (_faDebounce == null)
            {
                _faDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _faDebounce.Tick += async (s, a) => { _faDebounce.Stop(); await ApplyFaFilterAsync(); };
            }
            _faDebounce.Stop();
            _faDebounce.Start();
        }

        private void FaSource_Changed(object sender, RoutedEventArgs e) => FaFilter_Changed(sender, e);

        private ArchiveFilter BuildFaFilter()
        {
            var f = new ArchiveFilter
            {
                Text = TxtFaSearch.Text ?? "",
                Regex = TglFaRegex.IsChecked == true,
                Tag = TxtFaTag.Text ?? "",
                Pid = TxtFaPid.Text ?? "",
                FromMinute = _faFrom,
                ToMinute = _faTo,
            };
            var levels = FaLevelToggles.Where(t => t.IsChecked == true).Select(t => (string)t.Tag).ToList();
            if (levels.Count < 6) f.Levels = new HashSet<string>(levels);
            var files = _faArchive.LogFiles.ToList();
            if (files.Any(x => !x.Included)) f.Sources = new HashSet<int>(files.Where(x => x.Included).Select(x => x.SourceId));
            return f;
        }

        private async Task ApplyFaFilterAsync()
        {
            if (_faArchive == null) return;
            _faFilterCts?.Cancel();
            var cts = new CancellationTokenSource();
            _faFilterCts = cts;
            var filter = BuildFaFilter();
            var archive = _faArchive;
            List<FileLogEntry> view;
            try
            {
                view = await Task.Run(() => archive.Filter(filter, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ArgumentException)
            {
                TxtFaCount.Text = Loc.T("⚠️ Expression régulière invalide");
                return;
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { return; }
            if (cts.IsCancellationRequested || archive != _faArchive) return;

            _faView = view;
            FaGrid.ItemsSource = view;
            TxtFaCount.Text = Loc.F("{0} ligne(s) affichée(s) sur {1}", view.Count.ToString("N0", CultureInfo.CurrentCulture), archive.Entries.Count.ToString("N0", CultureInfo.CurrentCulture));
            TxtFaPeriod.Text = _faFrom == int.MinValue ? "" : Loc.F("Période : {0} → {1}", LogArchive.MinuteText(_faFrom), LogArchive.MinuteText(_faTo).Substring(6));
            BtnFaAllPeriod.IsEnabled = _faFrom != int.MinValue;

            // Frise : sur les journaux cochés (sans les autres filtres), pour garder la vue d'ensemble.
            var timelineSource = filter.Sources == null ? archive.Entries : archive.Entries.Where(x => filter.Sources.Contains(x.SourceId)).ToList();
            _faBuckets = await Task.Run(() => archive.Timeline(timelineSource));
            DrawFaTimeline();

            // Défilement demandé (incident, frise, « voir autour »).
            int idx = -1;
            if (_faScrollToEntry != null) { idx = FindFaIndex(_faScrollToEntry.Index); _faScrollToEntry = null; }
            else if (_faScrollToMinute >= 0) { idx = view.FindIndex(x => x.Minute >= _faScrollToMinute); _faScrollToMinute = -1; }
            if (idx >= 0) ScrollFaTo(idx);
            ScheduleFaZoneRefresh();
        }

        /// <summary>Position dans la vue filtrée de la ligne d'index global donné (ou de la plus proche qui suit).</summary>
        private int FindFaIndex(int globalIndex)
        {
            int lo = 0, hi = _faView.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_faView[mid].Index >= globalIndex) { best = mid; hi = mid - 1; } else lo = mid + 1;
            }
            return best;
        }

        private void ScrollFaTo(int index)
        {
            if (index < 0 || index >= _faView.Count) return;
            var item = _faView[index];
            FaGrid.SelectedItem = item;
            FaGrid.UpdateLayout();
            FaGrid.ScrollIntoView(item);
            // Ligne visée au milieu de l'écran plutôt que collée en bas.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sv = FindVisualChild<ScrollViewer>(FaGrid);
                if (sv != null) sv.ScrollToVerticalOffset(Math.Max(0, index - sv.ViewportHeight / 2));
            }), DispatcherPriority.Background);
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var r = FindVisualChild<T>(child);
                if (r != null) return r;
            }
            return null;
        }

        private async void BtnFaClearFilters_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null) return;
            _faSuspendFilter = true;
            ResetFaFilterControls();
            foreach (var f in _faArchive.LogFiles) f.Included = true;
            _faSuspendFilter = false;
            _faFrom = int.MinValue; _faTo = int.MaxValue;
            _faScrollToEntry = FaGrid.SelectedItem as FileLogEntry;
            await ApplyFaFilterAsync();
        }

        // ------------------------------------------------------------------ Incident et période

        private void TxtFaIncident_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFaGoIncident_Click(sender, e);
        }

        private async void BtnFaGoIncident_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null) return;
            int t = _faArchive.ParseIncidentTime(TxtFaIncident.Text);
            if (t < 0)
            {
                TxtFaCount.Text = Loc.T("⚠️ Heure non reconnue (ex. 9h07, 09:07 ou 05/11 9h07)");
                return;
            }
            int w = int.TryParse((CmbFaWindow.SelectedItem as ComboBoxItem)?.Tag as string, out int x) ? x : 5;
            _faFrom = t - w; _faTo = t + w;
            _faScrollToMinute = t;
            await ApplyFaFilterAsync();
            await RunFaBeforeAsync(t, LogArchive.MinuteText(t));
        }

        private async void BtnFaAllPeriod_Click(object sender, RoutedEventArgs e)
        {
            _faFrom = int.MinValue; _faTo = int.MaxValue;
            _faScrollToEntry = FaGrid.SelectedItem as FileLogEntry;
            await ApplyFaFilterAsync();
        }

        // ------------------------------------------------------------------ Frise

        private void FaTimeline_SizeChanged(object sender, SizeChangedEventArgs e) => DrawFaTimeline();

        /// <summary>Largeur réservée à gauche de la frise pour le nom des couloirs.</summary>
        private const double FaLaneLabelWidth = 96;

        private void DrawFaTimeline()
        {
            if (FaTimeline == null) return;
            FaTimeline.Children.Clear();
            _faHereRect = null; _faHereMark = null;
            double W = FaTimeline.ActualWidth, h = FaTimeline.ActualHeight;
            if (_faBuckets.Count == 0 || W < FaLaneLabelWidth + 20 || h < 30) { TxtFaTimelineFrom.Text = ""; TxtFaTimelineTo.Text = ""; return; }
            double L = FaLaneLabelWidth, w = W - L;
            int lanes = LogInsights.LaneCount;
            double barsH = Math.Max(18, h * 0.42), laneH = (h - barsH - 4) / lanes;
            int maxLines = Math.Max(1, _faBuckets.Max(b => b.Lines)), maxErr = Math.Max(1, _faBuckets.Max(b => b.Errors));
            double bw = w / _faBuckets.Count;
            var okBrush = (Brush)FindResource("OkFg"); var badBrush = (Brush)FindResource("BadFg"); var warnBrush = (Brush)FindResource("WarnFg");
            var brand = (Brush)FindResource("Brand"); var muted = (Brush)FindResource("Muted"); var line = (Brush)FindResource("Line");
            var info = (Brush)FindResource("InfoFg");

            if (_faFrom != int.MinValue)
            {
                double x1 = MinuteToX(_faFrom, W), x2 = MinuteToX(_faTo + 1, W);
                var r = new Rectangle { Width = Math.Max(2, x2 - x1), Height = h, Fill = brand, Opacity = 0.15 };
                Canvas.SetLeft(r, x1); FaTimeline.Children.Add(r);
            }
            // Lignes et erreurs
            AddFaLabel(Loc.T("Lignes / erreurs"), 0, barsH, muted);
            for (int i = 0; i < _faBuckets.Count; i++)
            {
                var b = _faBuckets[i];
                double x = L + i * bw;
                if (b.Lines > 0)
                {
                    double lh = Math.Max(1, barsH * Math.Sqrt(b.Lines / (double)maxLines));
                    var r = new Rectangle { Width = Math.Max(1, bw - 1), Height = lh, Fill = okBrush, Opacity = 0.45 };
                    Canvas.SetLeft(r, x); Canvas.SetTop(r, barsH - lh); FaTimeline.Children.Add(r);
                }
                if (b.Errors > 0)
                {
                    double eh = Math.Max(2, barsH * 0.85 * Math.Sqrt(b.Errors / (double)maxErr));
                    var r = new Rectangle { Width = Math.Max(1, bw - 1), Height = eh, Fill = badBrush, Opacity = 0.8 };
                    Canvas.SetLeft(r, x); Canvas.SetTop(r, barsH - eh); FaTimeline.Children.Add(r);
                }
                if (b.Incidents > 0)
                {
                    FaTimeline.Children.Add(new Line { X1 = x + bw / 2, X2 = x + bw / 2, Y1 = 0, Y2 = h, Stroke = warnBrush, StrokeThickness = 2 });
                    var tb = new TextBlock { Text = "⚠", Foreground = warnBrush, FontSize = 12, FontWeight = FontWeights.Bold };
                    Canvas.SetLeft(tb, x + bw / 2 + 2); Canvas.SetTop(tb, 0); FaTimeline.Children.Add(tb);
                }
            }
            // Couloirs : un repère par tranche où le sujet apparaît (plus foncé = plus fréquent)
            var names = LogInsights.LaneNames;
            Brush[] laneBrush = { info, badBrush, warnBrush, (Brush)FindResource("BrandText"), warnBrush, badBrush };
            for (int l = 0; l < lanes; l++)
            {
                double y = barsH + 4 + l * laneH;
                FaTimeline.Children.Add(new Line { X1 = L, X2 = W, Y1 = y + laneH, Y2 = y + laneH, Stroke = line, StrokeThickness = 0.5 });
                AddFaLabel(names[l], y, laneH, muted);
                int maxLane = Math.Max(1, _faBuckets.Max(b => b.Lanes[l]));
                for (int i = 0; i < _faBuckets.Count; i++)
                {
                    int n = _faBuckets[i].Lanes[l];
                    if (n == 0) continue;
                    var r = new Rectangle { Width = Math.Max(2, bw - 1), Height = Math.Max(3, laneH - 3), Fill = laneBrush[l], Opacity = 0.35 + 0.65 * Math.Sqrt(n / (double)maxLane), ToolTip = names[l] + " : " + n + " · " + LogArchive.MinuteText(_faBuckets[i].From) };
                    Canvas.SetLeft(r, L + i * bw); Canvas.SetTop(r, y + 1.5); FaTimeline.Children.Add(r);
                }
            }
            // Signets
            foreach (var bm in _faBookmarks.Where(x => x.Entry != null && x.Entry.Minute >= 0))
            {
                double x = MinuteToX(bm.Entry.Minute, W);
                FaTimeline.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = barsH, Stroke = info, StrokeThickness = 2 });
                var tb = new TextBlock { Text = "🔖", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 10, ToolTip = bm.Note };
                Canvas.SetLeft(tb, x - 6); Canvas.SetTop(tb, barsH - 14); FaTimeline.Children.Add(tb);
            }
            if (FaGrid.SelectedItem is FileLogEntry sel && sel.Minute >= 0)
            {
                double x = MinuteToX(sel.Minute, W);
                FaTimeline.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = (Brush)FindResource("Ink"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } });
            }
            TxtFaTimelineFrom.Text = "          " + LogArchive.MinuteText(_faBuckets[0].From);
            TxtFaTimelineTo.Text = LogArchive.MinuteText(_faBuckets[_faBuckets.Count - 1].To);

            // « Vous êtes ici » : cadre sur la partie du relevé visible dans le tableau.
            _faHereRect = new Rectangle { Height = h, Stroke = (Brush)FindResource("Ink"), StrokeThickness = 1.5, Fill = brand, Opacity = 0.3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            _faHereMark = new Polygon { Points = new PointCollection { new Point(-6, 0), new Point(6, 0), new Point(0, 8) }, Fill = (Brush)FindResource("Ink"), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            FaTimeline.Children.Add(_faHereRect);
            FaTimeline.Children.Add(_faHereMark);
            UpdateFaHere();
        }

        private void AddFaLabel(string text, double top, double height, Brush brush)
        {
            var tb = new TextBlock { Text = text, Foreground = brush, FontSize = Math.Min(10.5, Math.Max(8, height * 0.7)), Width = FaLaneLabelWidth - 6, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
            Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, top + Math.Max(0, (height - tb.FontSize * 1.35) / 2));
            FaTimeline.Children.Add(tb);
        }

        /// <summary>Met à jour le repère « vous êtes ici » (frise) et le texte de la zone affichée.</summary>
        private void UpdateFaHere()
        {
            if (_faHereRect == null || _faBuckets.Count == 0 || _faVisibleFrom < 0)
            {
                if (TxtFaHere != null) TxtFaHere.Text = "";
                return;
            }
            double w = FaTimeline.ActualWidth;
            double x1 = MinuteToX(_faVisibleFrom, w), x2 = MinuteToX(_faVisibleTo + 1, w);
            double width = Math.Max(3, x2 - x1);
            double left = Math.Max(0, Math.Min(x1, w - width));
            Canvas.SetLeft(_faHereRect, left);
            _faHereRect.Width = width;
            _faHereRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(_faHereMark, left + width / 2);
            Canvas.SetTop(_faHereMark, 0);
            _faHereMark.Visibility = Visibility.Visible;
            string a = LogArchive.MinuteText(_faVisibleFrom), b = LogArchive.MinuteText(_faVisibleTo);
            TxtFaHere.Text = "▼ " + Loc.F("Vous êtes ici : {0}", a == b ? a : a + " → " + (a.Substring(0, 5) == b.Substring(0, 5) ? b.Substring(6) : b));
        }

        private void FaGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_faView == null || _faView.Count == 0) { _faVisibleFrom = _faVisibleTo = -1; UpdateFaHere(); return; }
            var sv = e.OriginalSource as ScrollViewer;
            if (sv == null) return;
            int first = Math.Max(0, Math.Min(_faView.Count - 1, (int)sv.VerticalOffset));
            int last = Math.Max(first, Math.Min(_faView.Count - 1, (int)(sv.VerticalOffset + sv.ViewportHeight)));
            int mf = -1, ml = -1;
            for (int i = first; i <= last && mf < 0; i++) if (_faView[i].Minute >= 0) mf = _faView[i].Minute;
            for (int i = last; i >= first && ml < 0; i--) if (_faView[i].Minute >= 0) ml = _faView[i].Minute;
            if (mf < 0) return;
            if (ml < mf) ml = mf;
            bool changed = mf != _faVisibleFrom || ml != _faVisibleTo;
            _faVisibleFrom = mf; _faVisibleTo = ml;
            UpdateFaHere();
            if (changed) ScheduleFaZoneRefresh();
        }

        private double MinuteToX(int minute, double W)
        {
            double L = FaLaneLabelWidth, w = Math.Max(1, W - L);
            int lo = _faBuckets[0].From, hi = _faBuckets[_faBuckets.Count - 1].To + 1;
            return L + Math.Max(0, Math.Min(w, (minute - lo) / (double)Math.Max(1, hi - lo) * w));
        }

        private async void FaTimeline_Click(object sender, MouseButtonEventArgs e)
        {
            if (_faBuckets.Count == 0 || _faArchive == null) return;
            double W = FaTimeline.ActualWidth, x = e.GetPosition(FaTimeline).X - FaLaneLabelWidth;
            if (x < 0) return;
            int i = Math.Max(0, Math.Min(_faBuckets.Count - 1, (int)(x / ((W - FaLaneLabelWidth) / _faBuckets.Count))));
            int minute = _faBuckets[i].From;
            if (_faFrom != int.MinValue && (minute < _faFrom || minute > _faTo))
            {
                _faFrom = int.MinValue; _faTo = int.MaxValue; // hors de la période affichée : on revient à tout le relevé
                _faScrollToMinute = minute;
                await ApplyFaFilterAsync();
                return;
            }
            int idx = _faView.FindIndex(v => v.Minute >= minute);
            if (idx >= 0) ScrollFaTo(idx);
        }

        // ------------------------------------------------------------------ Ligne sélectionnée

        private void FaGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = FaGrid.SelectedItem as FileLogEntry;
            BtnFaContext.IsEnabled = sel != null;
            BtnFaCopyLine.IsEnabled = sel != null;
            BtnFaBefore.IsEnabled = sel != null && sel.Minute >= 0;
            BtnFaBookmark.IsEnabled = sel != null;
            BtnFaSignature.IsEnabled = sel != null;
            TxtFaDetail.Text = sel == null ? Loc.T("Sélectionnez une ligne pour voir le message complet.")
                : $"{sel.Time}  [{sel.Source}]  {sel.PID}  {sel.Level}  {sel.Tag}\n{sel.Message}";
            DrawFaTimeline();
        }

        private async void BtnFaContext_Click(object sender, RoutedEventArgs e)
        {
            if (!(FaGrid.SelectedItem is FileLogEntry sel)) return;
            _faSuspendFilter = true;
            TxtFaSearch.Text = ""; TxtFaTag.Text = ""; TxtFaPid.Text = "";
            foreach (var t in FaLevelToggles) t.IsChecked = true;
            _faSuspendFilter = false;
            if (_faFrom != int.MinValue && (sel.Minute < _faFrom || sel.Minute > _faTo)) { _faFrom = int.MinValue; _faTo = int.MaxValue; }
            _faScrollToEntry = sel;
            await ApplyFaFilterAsync();
        }

        private void BtnFaCopyLine_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(TxtFaDetail.Text ?? ""); } catch { /* presse-papiers occupé */ }
        }

        /// <summary>Clic sur un constat de la page : la ligne concernée, filtres retirés si elle est masquée.</summary>
        private async void FaGoToEntry(FileLogEntry entry)
        {
            if (entry == null || _faArchive == null) return;
            int idx = FindFaIndex(entry.Index);
            if (idx >= 0 && _faView[idx] == entry) { ScrollFaTo(idx); return; }
            _faSuspendFilter = true;
            ResetFaFilterControls();
            foreach (var f in _faArchive.LogFiles) f.Included = true;
            _faSuspendFilter = false;
            _faFrom = int.MinValue; _faTo = int.MaxValue;
            _faScrollToEntry = entry;
            await ApplyFaFilterAsync();
        }

        /// <summary>Appelé par le clic sur une carte de constat : vrai si la carte appartient à cette page.</summary>
        private bool TryHandleFaFindingClick(object sender, LogFinding f)
        {
            if (!(sender is DependencyObject d) || FaFindingsList == null || !FaFindingsList.IsAncestorOf(d)) return false;
            FaGoToEntry(f?.FirstEntry as FileLogEntry);
            return true;
        }

        // ------------------------------------------------------------------ Constats de la zone regardée

        private bool FaFindingsFollowZone => CmbFaFindingsScope == null || CmbFaFindingsScope.SelectedIndex != 1;

        private int FaWindowMinutes => int.TryParse((CmbFaWindow.SelectedItem as ComboBoxItem)?.Tag as string, out int x) ? x : 5;

        /// <summary>Zone regardée : la période choisie (incident…), sinon ± la fenêtre autour des lignes affichées.</summary>
        private (int From, int To) CurrentFaZone()
        {
            if (_faFrom != int.MinValue) return (_faFrom, _faTo);
            if (_faVisibleFrom < 0) return (int.MinValue, int.MinValue);
            int w = FaWindowMinutes;
            return (_faVisibleFrom - w, _faVisibleTo + w);
        }

        private void ScheduleFaZoneRefresh()
        {
            if (_faArchive == null || !FaFindingsFollowZone) return;
            if (_faZoneTimer == null)
            {
                _faZoneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _faZoneTimer.Tick += async (s, a) => { _faZoneTimer.Stop(); await RefreshFaFindingsAsync(); };
            }
            _faZoneTimer.Stop();
            _faZoneTimer.Start();
        }

        private async void CmbFaFindingsScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_faArchive == null) return;
            await RefreshFaFindingsAsync(force: true);
        }

        /// <summary>Constats affichés : de tout le relevé, ou de la zone regardée (recalculés quand elle change).</summary>
        private async Task RefreshFaFindingsAsync(bool force = false)
        {
            if (_faArchive == null || _faFullAnalysis == null) return;
            var zone = FaFindingsFollowZone ? CurrentFaZone() : (int.MinValue, int.MinValue);
            if (zone.Item1 == int.MinValue)
            {
                _faZoneFrom = _faZoneTo = int.MinValue;
                ShowFaFindings(_faFullAnalysis, Loc.T("Tout le relevé"));
                return;
            }
            if (!force && zone.Item1 == _faZoneFrom && zone.Item2 == _faZoneTo) return;
            _faZoneFrom = zone.Item1; _faZoneTo = zone.Item2;

            _faZoneCts?.Cancel();
            var cts = new CancellationTokenSource();
            _faZoneCts = cts;
            var archive = _faArchive;
            var sources = archive.LogFiles.Any(f => !f.Included) ? new HashSet<int>(archive.LogFiles.Where(f => f.Included).Select(f => f.SourceId)) : null;
            int from = zone.Item1, to = zone.Item2;
            var sigs = _faSignatures; var trends = _faTrends; var sessions = _faSessions;
            string label = LogArchive.MinuteText(from) + " → " + LogArchive.MinuteText(to).Substring(6);
            try
            {
                var analysis = await Task.Run(() =>
                {
                    var lines = archive.Range(from, to, sources);
                    cts.Token.ThrowIfCancellationRequested();
                    return AnalyzeFa(archive, lines, from, to, sigs, trends, sessions);
                }, cts.Token);
                if (cts.IsCancellationRequested || archive != _faArchive) return;
                ShowFaFindings(analysis, label);
            }
            catch (OperationCanceledException) { }
        }

        private void ShowFaFindings(LogAnalysis analysis, string scope)
        {
            _faShownAnalysis = analysis;
            _faShownScope = scope;
            FaFindingsList.ItemsSource = analysis.Findings;
            int bad = analysis.Findings.Count(f => f.Severity == FindingSeverity.Bad), warn = analysis.Findings.Count(f => f.Severity == FindingSeverity.Warn);
            string head = Loc.F("Zone : {0} · {1} lignes.", scope, analysis.LinesAnalyzed.ToString("N0", CultureInfo.CurrentCulture));
            TxtFaFindingsSummary.Text = head + " " + (analysis.Findings.Count == 0 ? Loc.T("Aucun problème repéré.")
                : Loc.F("{0} problème(s) important(s), {1} point(s) à surveiller. Cliquez sur un constat pour voir la ligne.", bad, warn));
        }

        // ------------------------------------------------------------------ Lignes en grand

        private void TglFaWide_Changed(object sender, RoutedEventArgs e)
        {
            if (FaLeftColumn == null) return;
            bool wide = TglFaWide.IsChecked == true;
            if (wide)
            {
                if (FaLeftColumn.Width.Value > 0) _faLeftWidth = FaLeftColumn.Width;
                if (FaRightColumn.Width.Value > 0) _faRightWidth = FaRightColumn.Width;
                FaLeftColumn.Width = new GridLength(0); FaRightColumn.Width = new GridLength(0);
                FaLeftSplit.Width = new GridLength(0); FaRightSplit.Width = new GridLength(0);
            }
            else
            {
                FaLeftColumn.Width = _faLeftWidth; FaRightColumn.Width = _faRightWidth;
                FaLeftSplit.Width = new GridLength(8); FaRightSplit.Width = new GridLength(8);
            }
            FaLeftPanel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
            FaRightPanel.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ Panneau de droite

        private void FaPanel_Checked(object sender, RoutedEventArgs e)
        {
            if (FaFindingsPanel == null) return;
            FaFindingsPanel.Visibility = RbFaFindings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaFilePanel.Visibility = RbFaFile.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaDevicePanel.Visibility = RbFaDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaComparePanel.Visibility = RbFaCompare.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaBeforePanel.Visibility = RbFaBefore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaAppPanel.Visibility = RbFaApp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaSessionsPanel.Visibility = RbFaSessions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaBookmarksPanel.Visibility = RbFaBookmarks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            FaSignaturesPanel.Visibility = RbFaSignatures.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void FaOtherFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(FaOtherFilesList.SelectedItem is ArchiveFile f) || _faArchive == null) return;
            _faOpenFile = f;
            RbFaFile.IsChecked = true;
            TxtFaFileTitle.Text = f.Icon + " " + f.FullName + " — " + f.KindText + " · " + f.SizeText;
            TxtFaFileContent.Text = Loc.T("⏳ Lecture…");
            var archive = _faArchive;
            try
            {
                string text = await Task.Run(() => archive.ReadText(f));
                if (_faOpenFile == f) TxtFaFileContent.Text = text;
            }
            catch (Exception ex) { TxtFaFileContent.Text = Loc.F("Lecture impossible : {0}", ex.Message); }
        }

        private void TxtFaFileSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnFaFileNext_Click(sender, e);
        }

        private void BtnFaFileNext_Click(object sender, RoutedEventArgs e)
        {
            string q = TxtFaFileSearch.Text ?? "", text = TxtFaFileContent.Text ?? "";
            if (q.Length == 0 || text.Length == 0) return;
            int start = TxtFaFileContent.SelectionStart + Math.Max(1, TxtFaFileContent.SelectionLength);
            int i = text.IndexOf(q, Math.Min(start, text.Length), StringComparison.OrdinalIgnoreCase);
            if (i < 0) i = text.IndexOf(q, StringComparison.OrdinalIgnoreCase); // reprise au début
            if (i < 0) { TxtFaFileTitle.Text = Loc.F("« {0} » introuvable dans ce fichier.", q); return; }
            TxtFaFileContent.Focus();
            TxtFaFileContent.Select(i, q.Length);
            TxtFaFileContent.ScrollToLine(TxtFaFileContent.GetLineIndexFromCharacterIndex(i));
        }

        // ------------------------------------------------------------------ Comparaison de deux relevés

        private async void BtnFaCompare_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null || _faBusy) return;
            var dlg = new OpenFileDialog
            {
                Title = Loc.T("Relevé à comparer (B)"),
                Filter = Loc.T("Relevés de logs (*.zip;*.txt;*.log)|*.zip;*.txt;*.log|Tous les fichiers (*.*)|*.*"),
            };
            if (dlg.ShowDialog(this) != true) return;
            SetFaBusy(true);
            RbFaCompare.IsChecked = true;
            TxtFaCompareSummary.Text = Loc.T("⏳ Lecture du relevé B…");
            var a = _faArchive;
            try
            {
                var progress = new Progress<string>(s => TxtFaCompareSummary.Text = s);
                var b = await Task.Run(() => LogArchive.Open(dlg.FileName, progress));
                _faCompare = b;
                var rows = await Task.Run(() => LogCompare.Compare(a.Entries, b.Entries));
                double ha = LogCompare.Hours(a.Entries), hb = LogCompare.Hours(b.Entries);
                int ea = a.Entries.Count(x => x.Level == "E" || x.Level == "F"), eb = b.Entries.Count(x => x.Level == "E" || x.Level == "F");
                FaCompareGrid.ItemsSource = rows;
                _faCompareRows = rows;
                TxtFaCompareSummary.Text = Loc.F("A : {0} ({1} h, {2} erreurs/h) · B : {3} ({4} h, {5} erreurs/h). {6} type(s) de message diffèrent nettement ; clic = les rechercher dans A.",
                    a.Title, ha.ToString("0.#", CultureInfo.CurrentCulture), (ea / ha).ToString("0", CultureInfo.CurrentCulture),
                    b.Title, hb.ToString("0.#", CultureInfo.CurrentCulture), (eb / hb).ToString("0", CultureInfo.CurrentCulture), rows.Count);
                _faCompareText = TxtFaCompareSummary.Text;
            }
            catch (Exception ex)
            {
                TxtFaCompareSummary.Text = Loc.F("Lecture impossible : {0}", ex.Message);
            }
            finally { SetFaBusy(false); }
        }

        private void FaCompareGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(FaCompareGrid.SelectedItem is LogCompareRow r)) return;
            // Le modèle de message devient une recherche : chiffres et adresses remplacés par un motif.
            _faSuspendFilter = true;
            TglFaRegex.IsChecked = true;
            TxtFaTag.Text = r.Tag;
            _faSuspendFilter = false;
            TxtFaSearch.Text = Regex.Escape(r.Template).Replace(@"\#", "#").Replace("#", "[0-9a-fA-Fx]+");
        }

        // ------------------------------------------------------------------ Export

        private void BtnFaExport_Click(object sender, RoutedEventArgs e)
        {
            if (BtnFaExport.ContextMenu == null) return;
            BtnFaExport.ContextMenu.PlacementTarget = BtnFaExport;
            BtnFaExport.ContextMenu.Placement = PlacementMode.Bottom;
            BtnFaExport.ContextMenu.IsOpen = true;
        }

        private string FaExportBaseName => FileNameHelper.Sanitize(System.IO.Path.GetFileNameWithoutExtension(_faArchive?.Title ?? "releve"));

        /// <summary>Filtres actifs, en clair (pour le rapport).</summary>
        private string FaFiltersText()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(TxtFaSearch.Text)) parts.Add(Loc.F("recherche « {0} »", TxtFaSearch.Text) + (TglFaRegex.IsChecked == true ? " (regex)" : ""));
            var levels = FaLevelToggles.Where(x => x.IsChecked == true).Select(x => (string)x.Tag).ToList();
            if (levels.Count < 6) parts.Add(Loc.F("niveaux {0}", string.Join(" ", levels)));
            if (!string.IsNullOrWhiteSpace(TxtFaTag.Text)) parts.Add(Loc.F("tag « {0} »", TxtFaTag.Text));
            if (!string.IsNullOrWhiteSpace(TxtFaPid.Text)) parts.Add(Loc.F("PID {0}", TxtFaPid.Text));
            if (_faFrom != int.MinValue) parts.Add(Loc.F("période {0} → {1}", LogArchive.MinuteText(_faFrom), LogArchive.MinuteText(_faTo)));
            return parts.Count == 0 ? Loc.T("aucun") : string.Join(" · ", parts);
        }

        /// <summary>Contenu commun aux rapports : zone affichée, constats, avant l'incident, sessions, appli, signets.</summary>
        private LogReport.Options BuildFaReportOptions()
        {
            bool zoned = _faShownAnalysis != null && _faShownAnalysis != _faFullAnalysis && _faZoneFrom != int.MinValue;
            return new LogReport.Options
            {
                Title = _faArchive.Title,
                Archive = _faArchive,
                Analysis = _faShownAnalysis ?? _faFullAnalysis,
                View = _faView,
                From = zoned ? _faZoneFrom : int.MinValue,
                To = zoned ? _faZoneTo : int.MaxValue,
                ScopeText = string.IsNullOrEmpty(_faShownScope) ? Loc.T("Tout le relevé") : _faShownScope,
                FiltersText = FaFiltersText(),
                Compare = _faCompareRows,
                CompareText = _faCompareText,
                AppVersion = AppUpdater.CurrentVersion.ToString(3),
                Before = _faBefore, BeforeTitle = _faBeforeLabel, Sessions = _faSessions, App = _faAppLife, Bookmarks = _faBookmarks,
            };
        }

        /// <summary>Rapport technique détaillé en HTML (s'ouvre dans le navigateur).</summary>
        private async void MenuFaExportReport_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null) return;
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Rapport technique détaillé"),
                Filter = Loc.T("Page web (*.html)|*.html"),
                FileName = FaExportBaseName + "_rapport_technique.html",
            };
            if (dlg.ShowDialog(this) != true) return;
            var opt = BuildFaReportOptions();
            string path = dlg.FileName;
            try
            {
                string html = await Task.Run(() => LogReport.BuildHtml(opt));
                File.WriteAllText(path, html, new UTF8Encoding(false));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📄 Rapport d'analyse enregistré : {1}", DateTime.Now, path));
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { /* pas de navigateur associé */ }
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.F("Export impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
        }

        /// <summary>Rapport technique détaillé en PDF : la page HTML imprimée par Edge (ou Chrome), sans fenêtre.</summary>
        private async void MenuFaExportPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null) return;
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Rapport technique détaillé"),
                Filter = Loc.T("Document PDF (*.pdf)|*.pdf"),
                FileName = FaExportBaseName + "_rapport_technique.pdf",
                InitialDirectory = Directory.Exists(GetReportsFolderPath()) ? GetReportsFolderPath() : "",
            };
            if (dlg.ShowDialog(this) != true) return;
            var opt = BuildFaReportOptions();
            string pdf = dlg.FileName;
            string html = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ADBTools_rapport_" + Guid.NewGuid().ToString("N") + ".html");
            SetFaBusy(true);
            try
            {
                string content = await Task.Run(() => LogReport.BuildHtml(opt));
                File.WriteAllText(html, content, new UTF8Encoding(false));
                string error = await Task.Run(() => LogWordReport.HtmlToPdf(html, pdf));
                if (error != null)
                {
                    await ShowCustomDialogAsync(Loc.T("🔬 Rapport technique"), Loc.F("PDF impossible : {0}\n\nUtilisez « Rapport technique détaillé (HTML) » puis Imprimer ▸ Enregistrer en PDF dans le navigateur.", error), Loc.T("OK"), "", false);
                    return;
                }
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔬 Rapport technique enregistré : {1}", DateTime.Now, pdf));
                ShowInExplorer(pdf);
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.F("Export impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
            finally
            {
                try { if (File.Exists(html)) File.Delete(html); } catch { }
                SetFaBusy(false);
            }
        }

        /// <summary>Rapport client au format Word, sur le modèle des rapports SAV et Wi-Fi (en-tête, logo, modèle de l'équipe).</summary>
        private async void MenuFaExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (_faArchive == null) return;
            string templatePath = ReportTemplates.GetTemplatePath(ReportKind.Logs);
            if (!File.Exists(templatePath))
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Template Word introuvable : {1}", DateTime.Now, templatePath));
                return;
            }
            bool ok1 = await ShowCustomDialogAsync(Loc.T("📝 Export Word — Société"), Loc.T("Nom de la société / du client pour l'en-tête du rapport :"), Loc.T("Suivant"), Loc.T("Annuler"), false, requiresInput: true, defaultInput: "");
            if (!ok1) return;
            string societe = TxtDialogInput.Text.Trim();
            bool ok2 = await ShowCustomDialogAsync(Loc.T("📝 Export Word — N° d'incident"), Loc.T("Numéro d'incident / de ticket pour l'en-tête du rapport :"), Loc.T("Générer le rapport"), Loc.T("Annuler"), false, requiresInput: true, defaultInput: "");
            if (!ok2) return;
            string incident = TxtDialogInput.Text.Trim();

            var opt = BuildFaReportOptions();
            SetFaBusy(true);
            try
            {
                // Frise dessinée pour l'impression (fond blanc), ici car le dessin WPF doit se faire sur ce fil.
                var src = opt.From != int.MinValue ? _faArchive.Range(opt.From, opt.To) : (IList<FileLogEntry>)_faArchive.Entries;
                var buckets = _faArchive.Timeline(src, 120);
                string png = buckets.Count == 0 ? "" : ReportChartRenderer.RenderTimeline(buckets, LogInsights.LaneNames,
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ADBTools_frise_" + Guid.NewGuid().ToString("N") + ".png"));
                string folder = GetReportsFolderPath();
                string output = await Task.Run(() => LogWordReport.Generate(templatePath, folder, societe, incident, opt, png));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📝 Rapport Word d'analyse des logs généré : {1}", DateTime.Now, output));
                var analysis = opt.Analysis;
                RecordHistory(new HistoryEntry
                {
                    Type = "Analyse de logs",
                    Serial = _faArchive.Device.FirstOrDefault(p => p.Key == Loc.T("N° de série")).Value ?? "",
                    Model = _faArchive.Device.FirstOrDefault(p => p.Key == Loc.T("Modèle")).Value ?? _faArchive.Device.FirstOrDefault(p => p.Key == Loc.T("Terminal")).Value ?? "",
                    Client = societe,
                    Incident = incident,
                    Technician = AppSettings.Current.ReportTechnician ?? "",
                    Summary = Loc.F("{0} problème(s) important(s), {1} point(s) à surveiller — {2}",
                        analysis?.Findings.Count(f => f.Severity == FindingSeverity.Bad) ?? 0, analysis?.Findings.Count(f => f.Severity == FindingSeverity.Warn) ?? 0, opt.ScopeText),
                }, output);
                ShowInExplorer(output);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur génération du rapport Word : {1}", DateTime.Now, ex.Message));
                await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.F("Export impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
            finally { SetFaBusy(false); }
        }

        private void MenuFaExportTxt_Click(object sender, RoutedEventArgs e) => ExportFaLines(false);
        private void MenuFaExportCsv_Click(object sender, RoutedEventArgs e) => ExportFaLines(true);

        private async void ExportFaLines(bool csv)
        {
            if (_faArchive == null || _faView.Count == 0) return;
            var dlg = new SaveFileDialog
            {
                Title = Loc.T("Exporter les lignes affichées"),
                Filter = csv ? Loc.T("Fichier CSV (*.csv)|*.csv") : Loc.T("Fichier texte (*.txt)|*.txt"),
                FileName = FaExportBaseName + (csv ? "_lignes.csv" : "_lignes.txt"),
            };
            if (dlg.ShowDialog(this) != true) return;
            var view = _faView;
            string path = dlg.FileName, filters = FaFiltersText(), title = _faArchive.Title;
            try
            {
                await Task.Run(() =>
                {
                    // CSV : séparateur « ; » et BOM pour une ouverture directe dans Excel (version française).
                    using (var w = new StreamWriter(path, false, new UTF8Encoding(csv)))
                    {
                        if (csv)
                        {
                            w.WriteLine(string.Join(";", Loc.T("Heure"), Loc.T("Fichier"), "PID", Loc.T("Niveau"), Loc.T("Tag"), Loc.T("Message")));
                            foreach (var x in view)
                                w.WriteLine(string.Join(";", Csv(x.Time), Csv(x.Source), Csv(x.PID), Csv(x.Level), Csv(x.Tag), Csv(x.Message)));
                        }
                        else
                        {
                            w.WriteLine("# " + title + " — " + Loc.F("{0} ligne(s), filtres : {1}", view.Count, filters));
                            foreach (var x in view) w.WriteLine($"{x.Time} {x.PID,6} {x.Level} {x.Tag}: {x.Message}   [{x.Source}]");
                        }
                    }
                });
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 {1} ligne(s) exportée(s) : {2}", DateTime.Now, view.Count, path));
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync(Loc.T("🗂️ Analyse de fichiers"), Loc.F("Export impossible : {0}", ex.Message), Loc.T("OK"), "", false);
            }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
