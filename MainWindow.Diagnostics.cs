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
using System.Windows.Media.Imaging;
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
        // === ONGLET 7 : DIAGNOSTICS SAV (RMA) =====
        // ==========================================

        private readonly DiagnosticService _diagnosticService;
        private readonly ScreenTestService _screenTestService;
        private System.Collections.ObjectModel.ObservableCollection<DiagComponent> _diagList;
        private readonly AdbStreamProcess _getEventProcess = new AdbStreamProcess();
        private bool _isLabListening = false;
        private DateTime _lastKeyPressTime = DateTime.Now;
        private int _sameKeyCounter = 0;
        private string _lastKeyCode = "";

        // Terminal réellement diagnostiqué (mémorisé au lancement d'un test). Les exports (JSON, Word) s'en
        // servent au lieu de relire la sélection courante, qui peut avoir changé ou valoir
        // "Aucun appareil détecté..." si le terminal a été débranché avant l'export.
        private string _diagSerial;

        /// <summary>Serial de l'appareil sélectionné s'il est réellement connecté et autorisé, sinon null
        /// (l'entrée "Aucun appareil détecté..." de la liste n'est pas un vrai terminal).</summary>
        private string GetSelectedValidSerial()
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            return dev != null && dev.State == "device" ? dev.Serial : null;
        }

        /// <summary>Serial à inscrire dans un rapport/nom de fichier du module Diagnostics.</summary>
        private string GetDiagReportSerial() => _diagSerial ?? GetSelectedValidSerial() ?? "Offline";

        /// <summary>Mémorise le terminal diagnostiqué ; prévient si la session en cours concernait déjà un autre terminal.</summary>
        private void RememberDiagnosedDevice(string serial)
        {
            if (_diagSerial != null && _diagSerial != serial)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Ce diagnostic contenait déjà des résultats pour {1}. ", DateTime.Now, _diagSerial) +
                                      Loc.T("Cliquez sur « Nouveau test » avant de diagnostiquer un autre terminal pour ne pas mélanger les rapports."));
            }
            _diagSerial = serial;
        }

        // --- Repli des panneaux (inspecteur / Laboratoire) ---
        private bool _isInspectorCollapsed = false;
        private bool _isLabCardCollapsed = false;

        // --- Historique des boutons physiques ---
        private readonly KeyPressHistory _keyPressHistory = new KeyPressHistory();

        // --- Grille de zones tactiles (pixels/zones morts) ---
        private TouchZoneGrid _touchZoneGrid;
        private Border[,] _touchGridCells;
        private (int MinX, int MaxX, int MinY, int MaxY) _touchCalibration;
        private int? _lastRawTouchX;
        private int? _lastRawTouchY;
        private bool _touchFramePending;

        private void InitializeDiagChecklist()
        {
            _diagList = new System.Collections.ObjectModel.ObservableCollection<DiagComponent>
            {
                new DiagComponent { SystemId = "auth", StatusIcon = "⚪", ComponentName = "Autorisation ADB & Connexion", Details = "" },
                new DiagComponent { SystemId = "root", StatusIcon = "⚪", ComponentName = "Privilèges Root", Details = "" },
                new DiagComponent { SystemId = "wifi", StatusIcon = "⚪", ComponentName = "Interface Wi-Fi", Details = "" },
                new DiagComponent { SystemId = "ping", StatusIcon = "⚪", ComponentName = "Ping Réseau (Local & Internet)", Details = "" },
                new DiagComponent { SystemId = "battery", StatusIcon = "⚪", ComponentName = "État & Santé Batterie", Details = "" },
                new DiagComponent { SystemId = "storage", StatusIcon = "⚪", ComponentName = "Stockage Interne", Details = "" },
                new DiagComponent { SystemId = "sensors", StatusIcon = "⚪", ComponentName = "Capteurs (Accéléro/Gyro/Proximité/Lumière)", Details = "" },
                new DiagComponent { SystemId = "thermal", StatusIcon = "⚪", ComponentName = "Diagnostic Thermique", Details = "" },
                new DiagComponent { SystemId = "crashes", StatusIcon = "⚪", ComponentName = "Historique Crashs / ANR", Details = "" },
                new DiagComponent { SystemId = "keys", StatusIcon = "⚪", ComponentName = "Boutons Physiques (Test Manuel)", Details = "Testez les boutons dans le Laboratoire en bas à droite." },
                new DiagComponent { SystemId = "touch", StatusIcon = "⚪", ComponentName = "Dalle Tactile (Test Manuel)", Details = "Testez le tactile dans le Laboratoire en bas à droite." },
                new DiagComponent { SystemId = "scanner", StatusIcon = "⚪", ComponentName = "Optique Scanner (Test Manuel)", Details = "Déclenchez la gâchette dans le Laboratoire (détection automatique du signal) et vérifiez visuellement que le laser/illuminateur s'allume et décode un code-barres réel." },
                new DiagComponent { SystemId = "screen", StatusIcon = "⚪", ComponentName = "Écran : Pixels Morts / Brûlure (Test Manuel)", Details = "Utilisez les mires de couleur dans le Laboratoire en bas à droite." }
            };
            DiagChecklistGrid.ItemsSource = _diagList;
            KeyHistoryList.ItemsSource = _keyPressHistory.Entries;
        }

        private void DiagChecklistGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DiagChecklistGrid.SelectedItem is DiagComponent item)
            {
                TxtDiagTitle.Text = item.DisplayName;
                TxtDiagDetails.Text = string.IsNullOrEmpty(item.Details) ? Loc.T("Aucune donnée extraite.") : Loc.TranslateLines(item.Details);
                TxtDiagComment.Text = item.Comment;
                GridDiagValidation.Visibility = Visibility.Visible;

                // Sélectionner un test doit toujours le rendre visible, même si le technicien avait replié
                // l'inspecteur entre-temps pour travailler plus au large dans le Laboratoire.
                if (_isInspectorCollapsed) SetInspectorCollapsed(false);
            }
            else
            {
                GridDiagValidation.Visibility = Visibility.Collapsed;
            }
        }

        // --- REPLI DES PANNEAUX (inspecteur / Laboratoire) ---
        private void BtnToggleInspector_Click(object sender, RoutedEventArgs e) => SetInspectorCollapsed(!_isInspectorCollapsed);

        private void SetInspectorCollapsed(bool collapsed)
        {
            _isInspectorCollapsed = collapsed;
            InspectorBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            BtnToggleInspector.Content = collapsed ? "▸" : "▾";
            BtnToggleInspector.ToolTip = collapsed ? Loc.T("Afficher") : Loc.T("Réduire");
        }

        private void BtnToggleLabCard_Click(object sender, RoutedEventArgs e) => SetLabCardCollapsed(!_isLabCardCollapsed);

        private void SetLabCardCollapsed(bool collapsed)
        {
            _isLabCardCollapsed = collapsed;

            LabBodyGetEvent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            LabBodyScreenTest.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;

            // La ligne passe de "*" (occupe tout l'espace restant) à "Auto" (juste l'en-tête) : ça rend
            // immédiatement toute la hauteur libérée à l'inspecteur au-dessus, sans le pousser hors champ.
            LabRowDefinition.Height = collapsed ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            BtnToggleLabCard.Content = collapsed ? "▸" : "▾";
            BtnToggleLabCard.ToolTip = collapsed ? Loc.T("Afficher le Laboratoire") : Loc.T("Réduire le Laboratoire");
        }

        // --- VALIDATION MANUELLE ---
        private void BtnDiagPass_Click(object sender, RoutedEventArgs e) { UpdateDiagStatus("✅"); }
        private void BtnDiagFail_Click(object sender, RoutedEventArgs e) { UpdateDiagStatus("❌"); }

        private void UpdateDiagStatus(string status)
        {
            if (DiagChecklistGrid.SelectedItem is DiagComponent item)
            {
                item.StatusIcon = status;
                item.Comment = TxtDiagComment.Text;
                DiagChecklistGrid.Items.Refresh(); // Rafraîchit visuellement le tableau
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🩺 {1} marqué comme {2}", DateTime.Now, item.DisplayName, status));
            }
        }

        /// <summary>
        /// Capture le rendu visuel actuel d'un élément WPF (grille tactile, journal des touches...) en PNG
        /// dans le dossier de captures, pour insertion ultérieure dans le rapport Word. Ne dépend d'aucune
        /// bibliothèque externe (RenderTargetBitmap est natif WPF).
        /// </summary>
        private string CaptureVisualToFile(FrameworkElement element, string tag)
        {
            if (element == null || element.ActualWidth <= 0 || element.ActualHeight <= 0) return "";

            try
            {
                int width = (int)Math.Ceiling(element.ActualWidth);
                int height = (int)Math.Ceiling(element.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(element);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                string serial = FileNameHelper.Sanitize(GetDiagReportSerial());
                string path = System.IO.Path.Combine(GetCaptureFolderPath(), $"Diag_{tag}_{serial}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                using (var fs = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(fs);
                }
                return path;
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Capture visuelle impossible : {1}", DateTime.Now, ex.Message));
                return "";
            }
        }

        // --- NOUVEAU TEST (RÉINITIALISATION COMPLÈTE) ---
        private async void BtnResetDiagnostic_Click(object sender, RoutedEventArgs e)
        {
            bool confirm = await ShowCustomDialogAsync(
                Loc.T("🔄 Nouveau test"),
                Loc.T("Réinitialiser entièrement le diagnostic pour repartir de zéro ?\n\nCeci efface les statuts et commentaires (Auto + Manuel), la grille tactile et le journal des touches. Le terminal sélectionné n'est pas affecté."),
                Loc.T("Réinitialiser"), Loc.T("Annuler"), true);
            if (!confirm) return;

            if (_isLabListening)
            {
                ToggleLabListen.IsChecked = false;
                ToggleLabListen.Content = Loc.T("Démarrer l'écoute");
                StopGetEventLab();
            }

            // La session de diagnostic à laquelle appartiennent d'éventuelles captures (grille tactile...)
            // se termine réellement ici : c'est le bon moment pour les supprimer, plutôt qu'à chaque export
            // Word (qui doit rester rejouable sans perdre la capture si on corrige juste Société/N° d'incident).
            if (_diagList != null)
            {
                foreach (var item in _diagList)
                {
                    if (string.IsNullOrEmpty(item.ImagePath)) continue;
                    try { if (System.IO.File.Exists(item.ImagePath)) System.IO.File.Delete(item.ImagePath); }
                    catch { /* Non bloquant */ }
                }
            }

            InitializeDiagChecklist();
            _keyPressHistory.Reset();
            if (_touchZoneGrid != null) BuildTouchZoneGrid(_touchZoneGrid.Columns, _touchZoneGrid.Rows);

            TxtLabLastKey.Text = "-";
            TxtLabTouchX.Text = "X: 0000";
            TxtLabTouchY.Text = "Y: 0000";
            AlertStuckKey.Visibility = Visibility.Collapsed;
            _sameKeyCounter = 0;
            _lastKeyCode = "";
            _lastRawTouchX = null;
            _lastRawTouchY = null;
            _touchFramePending = false;
            _diagSerial = null;

            GridDiagValidation.Visibility = Visibility.Collapsed;
            TxtDiagTitle.Text = Loc.T("Sélectionnez un composant à tester");

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔄 Nouveau diagnostic : réinitialisation complète (Auto + Manuel).", DateTime.Now));
        }

        // --- CHECK-UP AUTOMATIQUE ---
        // La logique de chaque test (auth/root/wifi/ping/battery/storage) vit dans Core/DiagnosticService.cs :
        // cette méthode ne fait plus qu'orchestrer la boucle et afficher les résultats.
        private async void BtnRunAutoCheck_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Branchez un terminal pour le Check-up.", DateTime.Now));
                return;
            }

            RememberDiagnosedDevice(dev.Serial);

            BtnRunAutoCheck.IsEnabled = false;
            BtnRunAutoCheck.Content = Loc.T("⏳ Analyse en cours...");

            bool deviceLost = false;

            foreach (var item in _diagList)
            {
                if (item.SystemId == "keys" || item.SystemId == "touch" || item.SystemId == "scanner" || item.SystemId == "screen") continue; // Tests manuels ignorés

                // 🔌 Le terminal a été perdu en cours d'analyse : on ignore les tests restants au lieu
                // d'enchaîner des commandes vouées à l'échec.
                if (deviceLost)
                {
                    item.Details = "Test ignoré : le terminal a été déconnecté en cours d'analyse.";
                    item.StatusIcon = "🔌";
                    DiagChecklistGrid.Items.Refresh();
                    continue;
                }

                item.StatusIcon = "⏳";
                DiagChecklistGrid.Items.Refresh();

                try
                {
                    // Vérifie que le terminal est toujours présent avant de lancer ce test.
                    if (!await _diagnosticService.IsDeviceStillPresentAsync(dev.Serial))
                    {
                        deviceLost = true;
                        item.Details = "Terminal déconnecté : check-up interrompu à partir d'ici.";
                        item.StatusIcon = "🔌";
                        DiagChecklistGrid.Items.Refresh();

                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Check-up interrompu : terminal déconnecté pendant l'analyse.", DateTime.Now));
                        continue;
                    }

                    var result = await _diagnosticService.RunTestAsync(item.SystemId, dev.Serial);
                    if (result != null)
                    {
                        item.Details = result.Details;
                        item.StatusIcon = result.StatusIcon;
                    }
                }
                catch (Exception ex)
                {
                    item.Details = $"Erreur lors du test : {ex.Message}";
                    item.StatusIcon = "❌";
                }

                DiagChecklistGrid.Items.Refresh();
            }

            BtnRunAutoCheck.IsEnabled = true;
            BtnRunAutoCheck.Content = Loc.T("▶️ Lancer le Check-up Auto");
            AppendConsole(deviceLost
                ? Loc.F("\n[{0:HH:mm:ss}] 🩺 Check-up interrompu (terminal déconnecté).", DateTime.Now)
                : Loc.F("\n[{0:HH:mm:ss}] 🩺 Check-up Automatique terminé.", DateTime.Now));
            if (DiagChecklistGrid.SelectedItem != null) DiagChecklistGrid_SelectionChanged(null, null); // Force le rafraichissement de l'inspecteur
        }

        // --- LE LABORATOIRE MATÉRIEL (GETEVENT) ---
        private void ToggleLabListen_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                ToggleLabListen.IsChecked = false;
                return;
            }

            if (ToggleLabListen.IsChecked == true)
            {
                ToggleLabListen.Content = Loc.T("⏹️ Arrêter l'écoute");
                if (_isLabCardCollapsed) SetLabCardCollapsed(false); // démarrer un test doit rendre le Laboratoire visible
                RememberDiagnosedDevice(dev.Serial);
                StartGetEventLab(dev.Serial);
            }
            else
            {
                ToggleLabListen.Content = Loc.T("Démarrer l'écoute");
                StopGetEventLab();
            }
        }

        private async void StartGetEventLab(string serial)
        {
            StopGetEventLab();
            _isLabListening = true;

            // Calibration de la grille tactile AVANT de démarrer l'écoute : dimensions au ratio réel de
            // l'écran (via wm size, déjà utilisé pour le test d'écran) + plage réelle des coordonnées
            // brutes remontées par le digitizer (via getevent -lp), pour répartir correctement les touches.
            var (screenWidth, screenHeight) = await DeviceDisplayInfo.ResolveScreenSizeAsync(_adbManager, serial);
            var resLp = await _adbManager.ExecuteAsync($"-s {serial} shell getevent -lp");
            _touchCalibration = TouchCalibration.Parse(resLp.StandardOutput) ?? (0, screenWidth, 0, screenHeight);

            var (columns, rows) = TouchZoneGrid.ComputeDimensions(screenWidth, screenHeight);
            BuildTouchZoneGrid(columns, rows);

            try
            {
                _getEventProcess.Start(_adbExePath, $"-s {serial} shell getevent -l", rawLine => // Le paramètre -l traduit les codes hexa en mots clairs (ex: KEY_POWER)
                {
                    if (!_isLabListening) return;

                    var result = GetEventParser.Parse(rawLine.Trim());
                    if (result.Kind == GetEventKind.None) return;

                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (!_isLabListening) return;

                        if (result.Kind == GetEventKind.KeyPress)
                        {
                            TxtLabLastKey.Text = result.DisplayKeyName;

                            // Logique "Touche Coincée"
                            if (_lastKeyCode == result.RawKeyName && (DateTime.Now - _lastKeyPressTime).TotalMilliseconds < 100)
                                _sameKeyCounter++;
                            else
                                _sameKeyCounter = 0;

                            _lastKeyCode = result.RawKeyName;
                            _lastKeyPressTime = DateTime.Now;

                            AlertStuckKey.Visibility = _sameKeyCounter > 15 ? Visibility.Visible : Visibility.Collapsed;

                            // Historique des boutons : chaque touche distincte pressée s'ajoute à la liste,
                            // sans liste de boutons attendue prédéfinie (trop de modèles/marques différents).
                            if (_keyPressHistory.RecordPress(result.RawKeyName, result.DisplayKeyName))
                                KeyHistoryList.Items.Refresh();
                        }
                        else if (result.Kind == GetEventKind.TouchX)
                        {
                            TxtLabTouchX.Text = $"X: {result.Value:D4}";
                            _lastRawTouchX = result.Value;
                            _touchFramePending = true;
                        }
                        else if (result.Kind == GetEventKind.TouchY)
                        {
                            TxtLabTouchY.Text = $"Y: {result.Value:D4}";
                            _lastRawTouchY = result.Value;
                            _touchFramePending = true;
                        }
                        else if (result.Kind == GetEventKind.SyncReport && _touchFramePending)
                        {
                            // Une case n'est enregistrée qu'en fin de trame (SYN_REPORT), quand X ET Y sont à jour.
                            // Auparavant chaque ligne X ou Y enregistrait un point avec l'autre coordonnée encore
                            // périmée : des cases jamais touchées passaient au vert et masquaient une zone morte.
                            _touchFramePending = false;
                            RecordTouchInGrid();
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                });
            }
            catch (Exception ex) { AppendConsole(Loc.F("\n❌ Erreur GetEvent : {0}", ex.Message)); }
        }

        private void StopGetEventLab()
        {
            _isLabListening = false;
            _getEventProcess.Stop();

            TxtLabLastKey.Text = "-";
            AlertStuckKey.Visibility = Visibility.Collapsed;

            ApplyTouchGridResultToReport();
            ApplyKeyHistoryResultToReport();
            ApplyScannerResultToReport();

            // Capture un visuel de la grille tactile à l'arrêt de l'écoute, au moment où son résultat est
            // réellement figé pour le rapport (le test "touch" est validé automatiquement ici, pas via les
            // boutons Pass/Fail de la checklist). Le journal des touches physiques n'a pas besoin de capture
            // équivalente : son récapitulatif texte dans le tableau des modules est déjà complet.
            if (_diagList != null)
            {
                var touchItem = _diagList.FirstOrDefault(d => d.SystemId == "touch");
                if (touchItem != null && _touchZoneGrid != null && _touchZoneGrid.TouchedCount > 0)
                    touchItem.ImagePath = CaptureVisualToFile(TouchZoneGridPanel, "touch");
            }
        }

        // --- HISTORIQUE DES BOUTONS PHYSIQUES ---
        private void BtnResetKeyHistory_Click(object sender, RoutedEventArgs e)
        {
            _keyPressHistory.Reset();
        }

        /// <summary>
        /// Reporte automatiquement les boutons détectés dans la checklist SAV ("keys"). Aucune liste de
        /// boutons attendue n'étant connue à l'avance (le parc gère trop de marques/modèles différents),
        /// on informe simplement le technicien de ce qui a été détecté, à lui de juger si c'est complet.
        /// </summary>
        private void ApplyKeyHistoryResultToReport()
        {
            if (_keyPressHistory.Entries.Count == 0 || _diagList == null) return;

            var keysItem = _diagList.FirstOrDefault(d => d.SystemId == "keys");
            if (keysItem == null) return;

            var lines = _keyPressHistory.Entries.Select(entry => $"• {entry.DisplayName} : {entry.Count} appui(s)");
            keysItem.StatusIcon = "✅";
            keysItem.Details = $"--- BOUTONS DÉTECTÉS PENDANT LE TEST ---\n{string.Join("\n", lines)}\n\nℹ️ Vérifiez que tous les boutons attendus sur ce modèle apparaissent ci-dessus. Un bouton pressé mais absent de la liste n'a généré aucun signal (piste de bouton HS).\n";

            DiagChecklistGrid.Items.Refresh();
        }

        /// <summary>
        /// Isole, parmi les boutons déjà détectés, ceux qui ressemblent à une gâchette de scanner
        /// (voir ScanTriggerKeys). Ne confirme QUE la transmission du signal électrique/firmware : le
        /// laser/illuminateur et le décodage effectif d'un code-barres réel restent à vérifier visuellement,
        /// donc on ne marque jamais ❌ automatiquement ici (absence de détection ≠ preuve de panne, le
        /// technicien n'a peut-être simplement pas encore actionné la gâchette pendant ce test).
        /// </summary>
        private void ApplyScannerResultToReport()
        {
            if (_diagList == null) return;

            var triggerEntries = _keyPressHistory.Entries.Where(entry => ScanTriggerKeys.IsScanTrigger(entry.RawKeyName)).ToList();
            if (triggerEntries.Count == 0) return;

            var scannerItem = _diagList.FirstOrDefault(d => d.SystemId == "scanner");
            if (scannerItem == null) return;

            int totalPresses = triggerEntries.Sum(entry => entry.Count);
            var keyNames = string.Join(", ", triggerEntries.Select(entry => entry.DisplayName));

            scannerItem.StatusIcon = "✅";
            scannerItem.Details = $"--- GÂCHETTE SCANNER ---\n• Appuis détectés : {totalPresses} (touche(s) : {keyNames})\n\nℹ️ Ceci confirme uniquement que le signal de la gâchette est bien transmis au système. Vérifiez visuellement que le laser/illuminateur s'allume et décode correctement un code-barres réel : cette partie reste un contrôle manuel.\n";

            DiagChecklistGrid.Items.Refresh();
        }

        // --- GRILLE DE ZONES TACTILES ---
        private static SolidColorBrush TouchCellUntouchedBrush => (SolidColorBrush)Application.Current.FindResource("Surface2");
        private static SolidColorBrush TouchCellTouchedBrush => (SolidColorBrush)Application.Current.FindResource("OkFg");

        private void BuildTouchZoneGrid(int columns, int rows)
        {
            const double cellSize = 22;

            TouchZoneGridPanel.Children.Clear();
            TouchZoneGridPanel.Rows = rows;
            TouchZoneGridPanel.Columns = columns;
            TouchZoneGridPanel.Width = columns * cellSize;
            TouchZoneGridPanel.Height = rows * cellSize;

            _touchZoneGrid = new TouchZoneGrid(columns, rows);
            _touchGridCells = new Border[rows, columns];

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    var cell = new Border
                    {
                        Background = TouchCellUntouchedBrush,
                        BorderBrush = (Brush)FindResource("BorderColor"),
                        BorderThickness = new Thickness(0.5),
                        Margin = new Thickness(0.5)
                    };
                    TouchZoneGridPanel.Children.Add(cell);
                    _touchGridCells[r, c] = cell;
                }
            }

            UpdateTouchGridStats();
        }

        private void RecordTouchInGrid()
        {
            if (_touchZoneGrid == null || !_lastRawTouchX.HasValue || !_lastRawTouchY.HasValue) return;

            var cal = _touchCalibration;
            double normalizedX = (double)(_lastRawTouchX.Value - cal.MinX) / (cal.MaxX - cal.MinX);
            double normalizedY = (double)(_lastRawTouchY.Value - cal.MinY) / (cal.MaxY - cal.MinY);
            normalizedX = Math.Max(0, Math.Min(1, normalizedX));
            normalizedY = Math.Max(0, Math.Min(1, normalizedY));

            if (_touchZoneGrid.RecordTouch(normalizedX, normalizedY, out int row, out int col))
            {
                _touchGridCells[row, col].Background = TouchCellTouchedBrush;
                UpdateTouchGridStats();
            }
        }

        private void UpdateTouchGridStats()
        {
            if (_touchZoneGrid == null) return;
            TxtTouchGridStats.Text = Loc.F("Zones testées : {0} / {1}", _touchZoneGrid.TouchedCount, _touchZoneGrid.TotalCount);
        }

        private void BtnResetTouchGrid_Click(object sender, RoutedEventArgs e)
        {
            if (_touchZoneGrid == null) return;
            BuildTouchZoneGrid(_touchZoneGrid.Columns, _touchZoneGrid.Rows);
        }

        private bool _touchGridExpanded = false;
        private void BtnExpandTouchGrid_Click(object sender, RoutedEventArgs e)
        {
            _touchGridExpanded = !_touchGridExpanded;

            // Les deux panneaux sont réglés explicitement à la même valeur ici (pas de binding XAML
            // ActualHeight croisé entre eux : dans une ligne de Grid "Auto", ce genre de binding boucle
            // sur lui-même et sature le thread UI).
            double height = _touchGridExpanded ? 320 : 150;
            TouchGridViewbox.MaxHeight = height;
            KeyHistoryBorder.Height = height;

            BtnExpandTouchGrid.Content = _touchGridExpanded ? "⤡" : "⤢";
            BtnExpandTouchGrid.ToolTip = _touchGridExpanded ? Loc.T("Réduire la zone de test") : Loc.T("Agrandir la zone de test (grille + historique)");
        }

        /// <summary>
        /// Reporte automatiquement le résultat de la grille tactile dans la checklist SAV ("touch"), pour
        /// que le rapport exporté reflète directement les zones jamais atteintes, sans étape manuelle en plus.
        /// Le technicien garde la main : il peut toujours corriger le statut (PASS/FAIL) manuellement ensuite.
        /// </summary>
        private void ApplyTouchGridResultToReport()
        {
            if (_touchZoneGrid == null || _touchZoneGrid.TouchedCount == 0 || _diagList == null) return;

            var touchItem = _diagList.FirstOrDefault(d => d.SystemId == "touch");
            if (touchItem == null) return;

            var untouched = new List<string>();
            for (int r = 0; r < _touchZoneGrid.Rows; r++)
            {
                for (int c = 0; c < _touchZoneGrid.Columns; c++)
                {
                    if (!_touchZoneGrid.IsTouched(r, c))
                        untouched.Add($"L{r + 1}C{c + 1}");
                }
            }

            bool allTouched = untouched.Count == 0;
            touchItem.StatusIcon = allTouched ? "✅" : "⚠️";
            touchItem.Details = allTouched
                ? $"--- GRILLE TACTILE ---\n• Zones testées : {_touchZoneGrid.TouchedCount} / {_touchZoneGrid.TotalCount}\n• Aucune zone morte détectée : la totalité de l'écran a répondu au toucher.\n"
                : $"--- GRILLE TACTILE ---\n• Zones testées : {_touchZoneGrid.TouchedCount} / {_touchZoneGrid.TotalCount}\n⚠️ Zones jamais touchées lors du balayage ({untouched.Count}) : {string.Join(", ", untouched)}\n\nℹ️ Si le balayage n'a pas couvert tout l'écran, relancez un test complet avant de conclure à une zone morte.\n";

            DiagChecklistGrid.Items.Refresh();
        }

        // --- TEST D'ÉCRAN (PIXELS MORTS / BRÛLURE) ---
        private async void BtnScreenTestColor_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Connectez un terminal pour le test d'écran.", DateTime.Now));
                return;
            }

            string colorName = (sender as Button)?.Tag as string;
            Color color;
            switch (colorName)
            {
                case "Red": color = Colors.Red; break;
                case "Green": color = Colors.Lime; break;
                case "Blue": color = Colors.Blue; break;
                case "White": color = Colors.White; break;
                case "Black": color = Colors.Black; break;
                case "Gray": color = Color.FromRgb(128, 128, 128); break;
                default: return;
            }

            try
            {
                string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"adbtools_screentest_{colorName.ToLowerInvariant()}.png");
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🖥️ Affichage de la mire {1}...", DateTime.Now, colorName));
                RememberDiagnosedDevice(dev.Serial);
                string startError = await _screenTestService.ShowColorAsync(dev.Serial, colorName, color, tempPath);
                if (!string.IsNullOrEmpty(startError))
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ L'appareil n'a pas pu afficher l'image (probablement aucune visionneuse disponible) : {1}", DateTime.Now, startError));
                }
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur test d'écran : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void BtnScreenTestCleanup_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            bool confirm = await ShowCustomDialogAsync(Loc.T("🗑️ Nettoyer"), Loc.T("Supprimer les images de test d'écran envoyées sur le terminal ?"), Loc.T("Supprimer"), Loc.T("Annuler"), true);
            if (!confirm) return;

            try
            {
                await _screenTestService.CleanupAsync(dev.Serial);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🗑️ Images de test d'écran supprimées du terminal.", DateTime.Now));
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur nettoyage : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- EXPORTATION DU RAPPORT (JSON) ---
        private void BtnExportRma_Click(object sender, RoutedEventArgs e)
        {
            if (_diagList == null || _diagList.Count == 0) return;

            string serial = GetDiagReportSerial();

            try
            {
                string json = DiagnosticService.BuildRmaReportJson(serial, _diagList);

                string savePath = System.IO.Path.Combine(GetReportsFolderPath(), $"Rapport_RMA_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
                System.IO.File.WriteAllText(savePath, json);

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💾 Rapport JSON RMA généré : {1}", DateTime.Now, savePath));
                RecordHistory(ADBToolsNew.Core.DiagHistory.FromDiagnostic("Rapport RMA", serial, "", "", _diagList), savePath);
                ShowInExplorer(savePath);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur génération du rapport JSON : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- EXPORTATION DU RAPPORT (WORD) ---
        private async void BtnExportWord_Click(object sender, RoutedEventArgs e)
        {
            if (_diagList == null || _diagList.Count == 0) return;

            string serial = GetDiagReportSerial();

            string templatePath = ADBToolsNew.Core.ReportTemplates.GetTemplatePath(ADBToolsNew.Core.ReportKind.Sav); // modèle perso (Templates\) sinon modèle fourni
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📝 Modèle Word utilisé : ", DateTime.Now) + (ADBToolsNew.Core.ReportTemplates.HasUserTemplate(ADBToolsNew.Core.ReportKind.Sav) ? Loc.T("personnel (") + System.IO.Path.GetFileName(templatePath) + ")" : Loc.T("modèle fourni")));
            if (!System.IO.File.Exists(templatePath))
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Template Word introuvable : {1}", DateTime.Now, templatePath));
                return;
            }

            var untested = ADBToolsNew.Core.WordReportService.GetUntestedComponentNames(_diagList);
            if (untested.Count > 0)
            {
                string list = string.Join("\n", untested.Select(name => $"• {name}"));
                bool proceedAnyway = await ShowCustomDialogAsync(
                    Loc.T("⚠️ Tests incomplets"),
                    Loc.F("{0} test(s) n'ont pas encore été réalisés :\n\n{1}\n\nLe rapport sera généré avec ces tests marqués \"Non testé\". Générer quand même ?", untested.Count, list),
                    Loc.T("Générer quand même"), Loc.T("Annuler"), false);
                if (!proceedAnyway) return;
            }

            bool ok1 = await ShowCustomDialogAsync(Loc.T("📝 Export Word — Société"), Loc.T("Nom de la société / du client pour l'en-tête du rapport :"), Loc.T("Suivant"), Loc.T("Annuler"), false, requiresInput: true, defaultInput: "");
            if (!ok1) return;
            string societe = TxtDialogInput.Text.Trim();

            bool ok2 = await ShowCustomDialogAsync(Loc.T("📝 Export Word — N° d'incident"), Loc.T("Numéro d'incident / de ticket SAV pour l'en-tête du rapport :"), Loc.T("Générer le rapport"), Loc.T("Annuler"), false, requiresInput: true, defaultInput: "");
            if (!ok2) return;
            string incident = TxtDialogInput.Text.Trim();

            try
            {
                string outputPath = ADBToolsNew.Core.WordReportService.GenerateReport(templatePath, GetReportsFolderPath(), serial, societe, incident, _diagList);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📝 Rapport Word généré : {1}", DateTime.Now, outputPath));
                RecordHistory(ADBToolsNew.Core.DiagHistory.FromDiagnostic("Diagnostic SAV", serial, societe, incident, _diagList), outputPath);
                ShowInExplorer(outputPath);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur génération du rapport Word : {1}", DateTime.Now, ex.Message));
            }
        }
    }
}
