using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ADBToolsNew
{
    /// <summary>
    /// Fiche de diagnostic SAV : une ligne par élément du terminal (voir Core/DiagnosticSheet.cs). Les tests de l'appli
    /// ADB Tools Diag (lancés depuis le PC, depuis l'appli, ou récupérés ensuite) remplissent directement la fiche ;
    /// le résultat retenu et le commentaire restent modifiables, avec l'historique de chaque ligne.
    /// </summary>
    public partial class MainWindow
    {
        private DateTime _diagMoreLastClosed;

        /// <summary>Lignes reprises dans les rapports : les éléments absents du terminal n'y figurent pas.</summary>
        private List<DiagComponent> DiagReportItems() => _diagList?.Where(d => !d.Unavailable).ToList() ?? new List<DiagComponent>();

        private static void AddDiagHistory(DiagComponent line, string text)
        {
            if (line == null || string.IsNullOrEmpty(text)) return;
            // Même entrée que la précédente (reprise du terminal en deux temps) : pas de doublon.
            string last = line.History.Count > 0 ? line.History[line.History.Count - 1] : null;
            if (last != null && last.Length > 8 && last.Substring(8) == text) return;
            line.History.Add($"{DateTime.Now:HH:mm} · {text}");
        }

        private DiagComponent DiagLineForApp(string appTest) =>
            _diagList?.FirstOrDefault(d => d.SystemId == DiagnosticSheet.LineIdForAppTest(appTest));

        private void EnsureDevItems()
        {
            if (_devItems.Count == 0)
                foreach (var def in DeviceTestCatalog.All) _devItems.Add(new DeviceTestItem(def));
        }

        // ------------------------------------------------------------------ Test de l'appli → fiche

        /// <summary>Recopie l'état d'un test de l'appli dans sa ligne de la fiche (avec une entrée d'historique si donnée).</summary>
        private void SyncDevToDiag(DeviceTestItem it, string history)
        {
            if (_diagList == null || it == null) return;
            var line = DiagLineForApp(it.Id);
            if (line == null) return;
            bool appOnly = line.SystemId.StartsWith(DeviceTestCatalog.SystemIdPrefix, StringComparison.Ordinal);

            if (it.Status == "⏳")
            {
                if (line.StatusIcon != "⏳") line.StatusBeforeRun = line.StatusIcon;
                line.StatusIcon = "⏳";
            }
            else if (it.Status == "⚪" || it.Status == "➖")
            {
                // Pas de résultat de l'appli : on garde (ou on rétablit) celui de la fiche.
                if (line.StatusIcon == "⏳") line.StatusIcon = line.StatusBeforeRun ?? "⚪";
                else if (appOnly && it.Status == "⚪" && history != null) line.StatusIcon = "⚪";
            }
            else line.StatusIcon = it.Status;

            if (it.Status != "⏳") line.TerminalDetails = it.Details ?? "";
            if (it.Status != "⏳" && !string.IsNullOrEmpty(it.ResultJson) && it.ResultJson != line.TerminalJson)
            {
                line.TerminalJson = it.ResultJson;
                // Grille faite sur le terminal : image pour la fiche et le rapport Word.
                if (line.SystemId == "touch")
                {
                    var map = TouchMap.Parse(it.ResultJson);
                    if (map != null) { string png = SaveTouchMapPng(map); if (png != null) line.ImagePath = png; }
                }
            }
            string file = it.LocalFile;
            if (file != null && file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) line.ImagePath = file;
            if (file != null && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) line.AudioPath = file;
            if (appOnly) line.Unavailable = !it.Available;
            if (string.IsNullOrWhiteSpace(line.Comment) && !string.IsNullOrWhiteSpace(it.Comment)) line.Comment = it.Comment;
            AddDiagHistory(line, history);

            DiagChecklistGrid.Items.Refresh();
            if (DiagChecklistGrid.SelectedItem == line) RefreshDiagDetail();
            RefreshDiagProgress();
        }

        // ------------------------------------------------------------------ Affichage

        private void RefreshDiagProgress()
        {
            if (_diagList == null || TxtDiagSubtitle == null) return;
            var lines = _diagList.Where(d => !d.Unavailable).ToList();
            int done = lines.Count(d => DiagnosticSheet.IsDone(d.StatusIcon));
            int warn = lines.Count(d => d.StatusIcon == "⚠️"), bad = lines.Count(d => d.StatusIcon == "❌");
            string device = _devFeatures != null
                ? string.Join(" ", new[] { _devFeatures.Manufacturer, _devFeatures.Model }.Where(x => !string.IsNullOrWhiteSpace(x))) + (string.IsNullOrEmpty(_devFeatures.Android) ? "" : " · Android " + _devFeatures.Android)
                : (_diagSerial ?? GetSelectedValidSerial() ?? "");
            var parts = new List<string>();
            if (device.Length > 0) parts.Add(device);
            parts.Add(Loc.F("{0} test(s) fait(s) sur {1}", done, lines.Count));
            if (warn > 0) parts.Add(Loc.F("{0} à surveiller", warn));
            if (bad > 0) parts.Add(Loc.F("{0} non conforme(s)", bad));
            TxtDiagSubtitle.Text = string.Join(" · ", parts);
            DiagProgress.Value = lines.Count == 0 ? 0 : done / (double)lines.Count;
        }

        /// <summary>Panneau de droite : consigne, actions possibles (terminal, PC), mesure, résultat, commentaire, historique.</summary>
        private void RefreshDiagDetail()
        {
            var item = DiagChecklistGrid.SelectedItem as DiagComponent;
            if (item == null)
            {
                TxtDiagTitle.Text = Loc.T("Choisissez un élément dans la liste");
                DiagSourceBadge.Visibility = Visibility.Collapsed;
                TxtDiagInstruction.Text = Loc.T("Chaque ligne indique qui fait le test : le PC, l'appli sur le terminal, ou les deux au choix. Le résultat retenu et le commentaire restent modifiables à tout moment, même après une récupération.");
                DiagActions.Visibility = DiagLiveBox.Visibility = LabBodyGetEvent.Visibility = LabBodyScreenTest.Visibility = DiagMediaPanel.Visibility = DiagTerminalPreview.Visibility = Visibility.Collapsed;
                GridDiagValidation.Visibility = Visibility.Collapsed;
                TxtDiagDetails.Text = Loc.T("Aucune donnée pour l'instant.");
                return;
            }
            TxtDiagTitle.Text = item.DisplayName;
            TxtDiagSource.Text = item.Unavailable ? Loc.T("Absent sur ce terminal")
                : item.Source == "PC" ? Loc.T("Contrôle du PC")
                : item.Source == "terminal" ? Loc.T("Test sur le terminal")
                : Loc.T("Sur le terminal ou depuis le PC");
            DiagSourceBadge.Visibility = Visibility.Visible;
            TxtDiagInstruction.Text = item.Unavailable ? Loc.T("Ce terminal n'a pas ce matériel : la ligne n'apparaîtra pas dans le rapport.") : Loc.T(item.Instruction);

            bool connected = GetSelectedValidSerial() != null;
            bool running = item.AppTest != null && _devCurrent == item.AppTest;
            BtnDiagRunTerminal.Visibility = item.AppTest != null && !item.Unavailable ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagRunTerminal.IsEnabled = connected && _devCurrent == null;
            BtnDiagStopTerminal.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagRunPc.Visibility = item.PcCheck ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagRunPc.IsEnabled = connected;
            ToggleLabListen.Visibility = item.PcLive == "getevent" ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagScanPc.Visibility = item.PcLive == "scan" ? Visibility.Visible : Visibility.Collapsed;
            DiagActions.Visibility = new UIElement[] { BtnDiagRunTerminal, BtnDiagRunPc, ToggleLabListen, BtnDiagScanPc }.Any(b => b.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;
            // Cartes de l'écoute du PC : seulement celle de la ligne, et seulement si l'écoute tourne ou a déjà relevé quelque chose.
            bool listening = ToggleLabListen.IsChecked == true;
            bool showKeys = item.SystemId == "keys" && (listening || _keyPressHistory.Entries.Count > 0);
            bool showTouch = item.SystemId == "touch" && (listening || (_touchZoneGrid != null && _touchZoneGrid.TouchedCount > 0));
            KeysLabCard.Visibility = showKeys ? Visibility.Visible : Visibility.Collapsed;
            TouchLabCard.Visibility = showTouch ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(TouchLabCard, showTouch && !showKeys ? 0 : 2);
            Grid.SetColumnSpan(KeysLabCard, showKeys && !showTouch ? 3 : 1);
            Grid.SetColumnSpan(TouchLabCard, showTouch && !showKeys ? 3 : 1);
            LabBodyGetEvent.Visibility = showKeys || showTouch ? Visibility.Visible : Visibility.Collapsed;
            RefreshDiagTerminalPreview(item);
            LabBodyScreenTest.Visibility = item.PcLive == "screen" ? Visibility.Visible : Visibility.Collapsed;
            if (!running) DiagLiveBox.Visibility = Visibility.Collapsed;

            TxtDiagDetails.Text = item.StatusIcon == "⏳" ? Loc.T("Test en cours sur le terminal…")
                : string.IsNullOrWhiteSpace(item.Details) ? Loc.T("Aucune donnée pour l'instant.") : Loc.TranslateLines(item.Details);

            bool image = !string.IsNullOrEmpty(item.ImagePath) && File.Exists(item.ImagePath);
            bool audio = !string.IsNullOrEmpty(item.AudioPath) && File.Exists(item.AudioPath);
            DiagPhoto.Source = image ? LoadBitmapNoLock(item.ImagePath) : null;
            DiagPhoto.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            BtnDiagPlay.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            DiagMediaPanel.Visibility = image || audio ? Visibility.Visible : Visibility.Collapsed;

            GridDiagValidation.Visibility = Visibility.Visible;
            GridDiagValidation.IsEnabled = !item.Unavailable;
            foreach (var b in DiagVerdictPanel.Children.OfType<Button>())
            {
                bool selected = (b.Tag as string) == item.StatusIcon;
                b.BorderThickness = new Thickness(selected ? 2.5 : 1);
                b.Opacity = selected || !DiagnosticSheet.IsDone(item.StatusIcon) ? 1.0 : 0.55;
            }
            if (!TxtDiagComment.IsKeyboardFocusWithin) TxtDiagComment.Text = item.Comment ?? "";
            DiagHistoryList.ItemsSource = null;
            DiagHistoryList.ItemsSource = item.History.Count == 0 ? new List<string> { Loc.T("Aucun résultat pour l'instant.") } : item.History.AsEnumerable().Reverse().ToList();
        }

        /// <summary>Aperçu du relevé fait dans l'appli : grille tactile (zones touchées) ou touches pressées.</summary>
        private void RefreshDiagTerminalPreview(DiagComponent item)
        {
            DiagTerminalPreview.Visibility = DiagTerminalGridBox.Visibility = DiagTerminalKeys.Visibility = Visibility.Collapsed;
            if (string.IsNullOrEmpty(item.TerminalJson) || item.StatusIcon == "⏳") return;
            if (item.SystemId == "touch")
            {
                var map = TouchMap.Parse(item.TerminalJson);
                if (map == null) return;
                DiagTerminalGrid.Children.Clear();
                DiagTerminalGrid.Columns = map.Cols;
                DiagTerminalGrid.Rows = map.Rows;
                DiagTerminalGrid.Width = map.Cols * 22;
                DiagTerminalGrid.Height = map.Rows * 22;
                for (int i = 0; i < map.Cols * map.Rows; i++)
                    DiagTerminalGrid.Children.Add(new Border
                    {
                        Background = map.Touched(i) ? TouchCellTouchedBrush : TouchCellUntouchedBrush,
                        BorderBrush = (Brush)FindResource("BorderColor"),
                        BorderThickness = new Thickness(0.5),
                        Margin = new Thickness(0.5),
                    });
                TxtDiagTerminalPreviewTitle.Text = Loc.T("Grille balayée sur le terminal");
                TxtDiagTerminalPreviewNote.Text = Loc.F("Zones touchées : {0} / {1}", map.Covered, map.Cols * map.Rows);
                DiagTerminalGridBox.Visibility = Visibility.Visible;
                DiagTerminalPreview.Visibility = Visibility.Visible;
                // L'image de la grille sert déjà d'aperçu : pas de doublon dans le bloc des fichiers.
                if (item.ImagePath != null && item.ImagePath.Contains("Diag_touchapp_")) DiagPhoto.Visibility = Visibility.Collapsed;
            }
            else if (item.SystemId == "keys")
            {
                var keys = TerminalKeys(item.TerminalJson);
                if (keys.Count == 0) return;
                DiagTerminalKeys.Children.Clear();
                foreach (var k in keys)
                    DiagTerminalKeys.Children.Add(new Border
                    {
                        Background = (Brush)FindResource("Surface"),
                        BorderBrush = (Brush)FindResource("BorderColor"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 6, 6),
                        Child = new TextBlock
                        {
                            Text = k.Value > 1 ? $"{k.Key}  ×{k.Value}" : k.Key,
                            Foreground = (Brush)FindResource("Ink"),
                            FontWeight = FontWeights.SemiBold,
                        },
                    });
                TxtDiagTerminalPreviewTitle.Text = Loc.T("Touches pressées sur le terminal");
                TxtDiagTerminalPreviewNote.Text = Loc.F("{0} touche(s) différente(s) détectée(s).", keys.Count);
                DiagTerminalKeys.Visibility = Visibility.Visible;
                DiagTerminalPreview.Visibility = Visibility.Visible;
            }
        }

        private static List<KeyValuePair<string, int>> TerminalKeys(string json)
        {
            var list = new List<KeyValuePair<string, int>>();
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("keys", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
                    foreach (var k in arr.EnumerateArray())
                    {
                        string name = k.TryGetProperty("key", out var n) ? n.ToString() : "";
                        int count = k.TryGetProperty("count", out var c) && c.TryGetInt32(out int v) ? v : 1;
                        if (name.Length > 0) list.Add(new KeyValuePair<string, int>(name, count));
                    }
                }
            }
            catch { }
            return list;
        }

        /// <summary>Grille du terminal dessinée en PNG (sans passer par l'écran) pour le rapport.</summary>
        private string SaveTouchMapPng(TouchMap map)
        {
            try
            {
                const int cell = 22;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var pen = new Pen((Brush)FindResource("BorderColor"), 0.5);
                    for (int r = 0; r < map.Rows; r++)
                        for (int c = 0; c < map.Cols; c++)
                            dc.DrawRectangle(map.Touched(r * map.Cols + c) ? TouchCellTouchedBrush : TouchCellUntouchedBrush, pen,
                                new Rect(c * cell + 0.5, r * cell + 0.5, cell - 1, cell - 1));
                }
                var bmp = new RenderTargetBitmap(map.Cols * cell, map.Rows * cell, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                string path = Path.Combine(GetCaptureFolderPath(), $"Diag_touchapp_{FileNameHelper.Sanitize(GetDiagReportSerial())}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                using (var fs = new FileStream(path, FileMode.Create)) enc.Save(fs);
                return path;
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Image de la grille tactile impossible : {1}", DateTime.Now, ex.Message));
                return null;
            }
        }

        private void TxtDiagComment_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!(DiagChecklistGrid.SelectedItem is DiagComponent item)) return;
            if ((item.Comment ?? "") == (TxtDiagComment.Text ?? "")) return;
            item.Comment = TxtDiagComment.Text ?? "";
            if (item.AppTest != null)
            {
                var dev = _devItems.FirstOrDefault(d => d.Id == item.AppTest);
                if (dev != null) dev.Comment = item.Comment;
            }
        }

        private void BtnDiagPlay_Click(object sender, RoutedEventArgs e)
        {
            var f = (DiagChecklistGrid.SelectedItem as DiagComponent)?.AudioPath;
            if (string.IsNullOrEmpty(f) || !File.Exists(f)) return;
            try { new System.Media.SoundPlayer(f).Play(); }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Lecture impossible : {1}", DateTime.Now, ex.Message)); }
        }

        private void BtnDiagOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var item = DiagChecklistGrid.SelectedItem as DiagComponent;
            string f = !string.IsNullOrEmpty(item?.ImagePath) && File.Exists(item.ImagePath) ? item.ImagePath : item?.AudioPath;
            if (!string.IsNullOrEmpty(f) && File.Exists(f)) ShowInExplorer(f);
        }

        private void BtnDiagMore_Click(object sender, RoutedEventArgs e)
        {
            if ((DateTime.Now - _diagMoreLastClosed).TotalMilliseconds < 200) return;
            DiagMorePopup.IsOpen = true;
        }

        private void DiagMorePopup_Closed(object sender, EventArgs e) => _diagMoreLastClosed = DateTime.Now;

        // ------------------------------------------------------------------ Actions

        private async void BtnDiagRunTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (!(DiagChecklistGrid.SelectedItem is DiagComponent item) || item.AppTest == null) return;
            string serial = GetSelectedValidSerial();
            if (serial == null || _devCurrent != null) return;
            BtnDiagRunTerminal.IsEnabled = false;
            try
            {
                if (!await EnsureDevSessionAsync(serial)) return;
                var it = _devItems.FirstOrDefault(d => d.Id == item.AppTest);
                if (it == null) return;
                if (!it.Available) { RefreshDiagDetail(); return; }
                await RunDevTestAsync(it, "");
            }
            finally { RefreshDiagDetail(); }
        }

        private async void BtnDiagStopTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (_devCurrent == null || _devSerial == null) return;
            await SendDiagCommandAsync(_devSerial, "stop");
        }

        /// <summary>Refait un seul contrôle du PC (ligne choisie).</summary>
        private async void BtnDiagRunPc_Click(object sender, RoutedEventArgs e)
        {
            if (!(DiagChecklistGrid.SelectedItem is DiagComponent item) || !item.PcCheck) return;
            string serial = GetSelectedValidSerial();
            if (serial == null) return;
            RememberDiagnosedDevice(serial);
            BtnDiagRunPc.IsEnabled = false;
            string before = item.StatusIcon;
            item.StatusIcon = "⏳";
            DiagChecklistGrid.Items.Refresh();
            try
            {
                var result = await _diagnosticService.RunTestAsync(item.SystemId, serial);
                if (result != null)
                {
                    item.Details = result.Details;
                    item.StatusIcon = result.StatusIcon;
                    AddDiagHistory(item, Loc.F("Contrôle automatique du PC : {0}", DiagnosticSheet.StatusWord(result.StatusIcon)));
                }
                else item.StatusIcon = before;
            }
            catch (Exception ex)
            {
                item.Details = Loc.F("Erreur lors du test : {0}", ex.Message);
                item.StatusIcon = "❌";
            }
            DiagChecklistGrid.Items.Refresh();
            RefreshDiagDetail();
            RefreshDiagProgress();
        }

        /// <summary>« Tester sur le terminal » : l'appli enchaîne ses tests, la fiche se remplit en direct.</summary>
        private async void BtnDiagTerminalAll_Click(object sender, RoutedEventArgs e)
        {
            string serial = GetSelectedValidSerial();
            if (serial == null)
            {
                await ShowCustomDialogAsync(Loc.T("📱 Tester sur le terminal"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            BtnDiagTerminalAll.IsEnabled = false;
            try
            {
                if (!await EnsureDevSessionAsync(serial)) return;
                // Fiche sans résultat du terminal : l'appli repart de zéro ; sinon elle reprend aux tests non faits.
                bool fresh = !_devItems.Any(i => DiagnosticSheet.IsDone(i.Status) || i.Status == "❓");
                await _adbManager.ExecuteAsync(DiagApp.CommandArgs(serial, "run_all") + (fresh ? " -e reset 1" : ""));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📱 Tests lancés sur le terminal : la fiche se remplit au fil des résultats.", DateTime.Now));
            }
            finally { BtnDiagTerminalAll.IsEnabled = true; }
        }

        private async void BtnDiagImport_Click(object sender, RoutedEventArgs e)
        {
            string serial = GetSelectedValidSerial();
            if (serial == null)
            {
                await ShowCustomDialogAsync(Loc.T("📥 Récupérer du terminal"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            await ShowDiagImportAsync(serial);
        }

        // ------------------------------------------------------------------ Récupération des résultats de l'appli

        public sealed class DiagImportRow : INotifyPropertyChanged
        {
            private bool _selected;
            public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
            public string AppTest { get; set; }
            public string Name { get; set; }
            public string TerminalText { get; set; }
            public string SheetText { get; set; }
            public bool Conflict { get; set; }
            public List<DiagMessage> Messages { get; set; }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        private List<DiagImportRow> _diagImportRows;
        private string _diagImportSerial;

        /// <summary>Lit les résultats gardés par l'appli et ouvre la fenêtre de récupération (choix ligne par ligne).</summary>
        private async Task ShowDiagImportAsync(string serial)
        {
            string json;
            try { json = (await _adbManager.ExecuteAsync($"-s {serial} shell cat {DiagApp.ResultsRemotePath}", TimeSpan.FromSeconds(15))).StandardOutput ?? ""; }
            catch { json = ""; }
            var msgs = DiagApp.ParseSavedResults(json);
            if (msgs.Count == 0)
            {
                await ShowCustomDialogAsync(Loc.T("📥 Récupérer du terminal"), Loc.T("Aucun test enregistré dans l'appli sur ce terminal."), Loc.T("OK"), "", false);
                return;
            }
            if (_diagList == null) InitializeDiagChecklist();
            EnsureDevItems();
            var rows = new List<DiagImportRow>();
            foreach (var g in msgs.GroupBy(x => x.Message.Test))
            {
                var line = DiagLineForApp(g.Key);
                if (line == null) continue;
                var list = g.Select(x => x.Message).ToList();
                var verdict = list.LastOrDefault(m => m.Type == "VERDICT");
                var result = list.LastOrDefault(m => m.Type == "RESULT");
                string icon = DeviceTestText.StatusIcon(verdict?.Status ?? result?.Status ?? "");
                bool sheetHas = DiagnosticSheet.IsDone(line.StatusIcon) || line.StatusIcon == "❓";
                bool conflict = sheetHas && line.StatusIcon != icon;
                string lastPc = line.History.LastOrDefault(h => h.Contains(Loc.T("Modifié sur le PC")));
                rows.Add(new DiagImportRow
                {
                    AppTest = g.Key,
                    Name = line.DisplayName,
                    TerminalText = DiagnosticSheet.StatusWord(icon),
                    SheetText = sheetHas ? DiagnosticSheet.StatusWord(line.StatusIcon) + (lastPc != null ? " " + Loc.T("(modifié sur le PC)") : "") : "—",
                    Conflict = conflict,
                    Selected = !conflict && icon != "⚪",
                    Messages = list,
                });
            }
            _diagImportRows = rows;
            _diagImportSerial = serial;
            DiagImportList.ItemsSource = rows;
            TxtDiagImportInfo.Text = Loc.F("{0} test(s) faits dans l'appli ADB Tools Diag. Cochez ceux à reprendre dans la fiche.", rows.Count);
            NavDiag_Click(null, null);
            DiagImportOverlay.Visibility = Visibility.Visible;
        }

        private void BtnDiagImportCancel_Click(object sender, RoutedEventArgs e) => DiagImportOverlay.Visibility = Visibility.Collapsed;

        private async void BtnDiagImportApply_Click(object sender, RoutedEventArgs e)
        {
            DiagImportOverlay.Visibility = Visibility.Collapsed;
            if (_diagImportRows == null || _diagImportSerial == null) return;
            if (_devSerial == null) _devSerial = _diagImportSerial;
            RememberDiagnosedDevice(_diagImportSerial);
            int n = 0;
            foreach (var row in _diagImportRows.Where(r => r.Selected))
            {
                var it = _devItems.FirstOrDefault(d => d.Id == row.AppTest);
                if (it == null) continue;
                await ApplyDevMessagesAsync(it, row.Messages);
                SyncDevToDiag(it, Loc.F("Repris du terminal : {0}", DiagnosticSheet.StatusWord(it.Status)));
                n++;
            }
            _diagImportRows = null;
            RefreshDiagDetail();
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📥 {1} test(s) du terminal repris dans la fiche de diagnostic.", DateTime.Now, n));
        }
    }
}
