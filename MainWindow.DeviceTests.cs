using ADBToolsNew.Core;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Tests du terminal (Diagnostics SAV ▸ Laboratoire ▸ « Tests du terminal ») : l'appli de diagnostic détecte le
    /// matériel présent, puis chaque test est lancé à la demande. Tous sont facultatifs : seuls les tests faits
    /// (✅ ⚠️ ❌) sont reportés dans le diagnostic et le rapport ; un test non fait ou sans objet n'y figure pas.
    /// </summary>
    public partial class MainWindow
    {
        private readonly ObservableCollection<DeviceTestItem> _devItems = new ObservableCollection<DeviceTestItem>();
        private readonly AdbStreamProcess _devLogProcess = new AdbStreamProcess();
        private DeviceFeatures _devFeatures;
        private string _devSerial;
        private bool _devSession;
        private string _devCurrent;
        private bool _devBusy;
        private DispatcherTimer _devProbeTimer;

        private DeviceTestItem SelectedDevTest => DevTestsList.SelectedItem as DeviceTestItem;

        private void BtnOpenDeviceTests_Click(object sender, RoutedEventArgs e)
        {
            if (_devItems.Count == 0)
                foreach (var def in DeviceTestCatalog.All) _devItems.Add(new DeviceTestItem(def));
            DevTestsList.ItemsSource = _devItems;
            DeviceTestsOverlay.Visibility = Visibility.Visible;
            RefreshDevTestPanel();
        }

        private async void BtnDevTestsClose_Click(object sender, RoutedEventArgs e)
        {
            if (_devSession) await StopDeviceTestsAsync();
            DeviceTestsOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnDevTestsStop_Click(object sender, RoutedEventArgs e) => await StopDeviceTestsAsync();

        // ------------------------------------------------------------------ Session

        private async void BtnDevTestsStart_Click(object sender, RoutedEventArgs e)
        {
            if (_devBusy || _devSession) return;
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🧪 Tests du terminal"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            _devBusy = true;
            BtnDevTestsStart.IsEnabled = false;
            try
            {
                // Nouveau terminal : on repart de zéro.
                if (_devSerial != dev.Serial)
                    foreach (var it in _devItems) { it.Status = "⚪"; it.Details = ""; it.Comment = ""; it.LocalFile = null; it.Available = true; }

                SetDevStatus(Loc.T("Installation de l'appli de diagnostic sur le terminal…"));
                if (!await EnsureDiagAppAsync(dev.Serial, "tests")) { SetDevStatus(Loc.T("Installation de l'appli de diagnostic impossible.")); return; }
                _devSerial = dev.Serial;
                _devSession = true;
                RememberDiagnosedDevice(dev.Serial);
                // Tests déjà faits dans l'appli (utilisée seule) : ils complètent les tests vides.
                var imp = await ImportTerminalResultsAsync(dev.Serial, false);
                _devImportNote = imp.Imported > 0 ? " " + Loc.F("{0} test(s) déjà fait(s) sur le terminal repris.", imp.Imported) : "";

                _devLogProcess.Start(_adbExePath, DiagApp.LogcatArgs(dev.Serial), line =>
                {
                    var m = DiagMessage.Parse(line);
                    if (m != null) Dispatcher.InvokeAsync(() => OnDevMessage(m), DispatcherPriority.Background);
                });
                SetDevStatus(Loc.T("Détection du matériel du terminal…"));
                await SendDiagCommandAsync(dev.Serial, "probe");

                // Pas de réponse : écran verrouillé, ou appli bloquée par le terminal.
                _devProbeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                _devProbeTimer.Tick += (s, ev) =>
                {
                    _devProbeTimer.Stop();
                    if (_devFeatures == null && _devSession)
                        SetDevStatus(Loc.T("Le terminal ne répond pas : déverrouillez son écran, puis cliquez à nouveau sur « Préparer le terminal »."));
                };
                _devFeatures = null;
                _devProbeTimer.Start();
                BtnDevTestsStop.IsEnabled = true;
            }
            catch (Exception ex)
            {
                SetDevStatus(Loc.F("Démarrage impossible : {0}", ex.Message));
            }
            finally
            {
                _devBusy = false;
                BtnDevTestsStart.IsEnabled = !_devSession || _devFeatures == null;
                RefreshDevTestPanel();
            }
        }

        private async Task StopDeviceTestsAsync()
        {
            if (!_devSession) return;
            if (_chainActive) FinishChain(Loc.T("Enchaînement arrêté."));
            _devSession = false;
            _devCurrent = null;
            _devProbeTimer?.Stop();
            string serial = _devSerial;
            try { if (serial != null) await SendDiagCommandAsync(serial, "stop"); } catch { }
            await Task.Delay(300); // dernier résultat éventuel (clavier) avant de couper le journal
            try { _devLogProcess.Stop(); } catch { }
            await ReleaseDiagAppAsync("tests");
            foreach (var it in _devItems.Where(i => i.Status == "⏳")) it.Status = "⚪";
            BtnDevTestsStop.IsEnabled = false;
            BtnDevTestsStart.IsEnabled = true;
            SetDevStatus(Loc.T("Tests terminés : les résultats restent ci-dessous. L'appli reste sur le terminal."));
            RefreshDevTestPanel();
        }

        private string _devImportNote = "";

        /// <summary>
        /// Tests faits dans l'appli ADB Tools Diag (gardés sur le terminal) : repris dans la liste. Un test déjà fait ici est
        /// gardé, sauf si <paramref name="replaceMeasured"/> et qu'il n'a qu'une mesure automatique (pas de choix du technicien).
        /// </summary>
        private async Task<(int Imported, int Kept)> ImportTerminalResultsAsync(string serial, bool replaceMeasured)
        {
            string json;
            try
            {
                var res = await _adbManager.ExecuteAsync($"-s {serial} shell cat {DiagApp.ResultsRemotePath}", TimeSpan.FromSeconds(15));
                json = res.StandardOutput ?? "";
            }
            catch { return (0, 0); }
            var msgs = DiagApp.ParseSavedResults(json);
            if (msgs.Count == 0) return (0, 0);
            if (_devItems.Count == 0)
                foreach (var def in DeviceTestCatalog.All) _devItems.Add(new DeviceTestItem(def));
            if (_devSerial != serial)
            {
                foreach (var it in _devItems) { it.Status = "⚪"; it.Details = ""; it.Comment = ""; it.LocalFile = null; it.Available = true; it.ManualVerdict = false; }
                _devSerial = serial;
            }
            int imported = 0, kept = 0;
            foreach (var g in msgs.GroupBy(x => x.Message.Test))
            {
                var item = _devItems.FirstOrDefault(i => i.Id == g.Key);
                if (item == null) continue;
                bool empty = item.Status == "⚪" || item.Status == "⏳";
                if (!empty && !(replaceMeasured && !item.ManualVerdict)) { kept++; continue; }
                foreach (var (m, at) in g)
                {
                    if (m.Type == "RESULT")
                    {
                        item.Status = DeviceTestText.StatusIcon(m.Status);
                        item.Details = DeviceTestText.Result(m);
                        item.ManualVerdict = false;
                        if (m.Status == "na") item.Available = false;
                        string remote = m.Str("file");
                        if (!string.IsNullOrEmpty(remote)) item.LocalFile = await PullDevFileAsync(remote, item.Id);
                    }
                    else if (m.Type == "VERDICT")
                    {
                        string icon = DeviceTestText.StatusIcon(m.Status);
                        item.Status = icon;
                        item.ManualVerdict = icon != "⚪";
                        if (icon == "⚪") { item.Details = ""; item.LocalFile = null; }
                        else item.Details = DeviceTestText.Confirmed(item.Id, icon, item.Details);
                    }
                }
                imported++;
            }
            RefreshDevSummary();
            RefreshDevTestPanel();
            return (imported, kept);
        }

        private async void BtnDevTestsImport_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🧪 Tests du terminal"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            BtnDevTestsImport.IsEnabled = false;
            try { ShowImportStatus(await ImportTerminalResultsAsync(dev.Serial, false)); }
            finally { BtnDevTestsImport.IsEnabled = true; }
        }

        private void ShowImportStatus((int Imported, int Kept) r)
        {
            SetDevStatus(r.Imported + r.Kept == 0 ? Loc.T("Aucun test enregistré dans l'appli sur ce terminal.")
                : Loc.F("{0} test(s) récupéré(s) du terminal ; {1} déjà fait(s) ici, gardé(s).", r.Imported, r.Kept));
        }

        /// <summary>« Envoyer au PC » dans l'appli : la fenêtre des tests s'ouvre avec les résultats du terminal.</summary>
        private async void OnDiagAppExport(string serial)
        {
            BtnOpenDeviceTests_Click(null, null);
            ShowImportStatus(await ImportTerminalResultsAsync(serial, true));
        }

        private void StopDeviceTestsOnExit()
        {
            try { _diagWatch.Stop(); } catch { }
            _devSession = false;
            try { _devLogProcess.Stop(); } catch { }
        }

        private void SetDevStatus(string text)
        {
            if (TxtDevTestsStatus != null) TxtDevTestsStatus.Text = text;
        }

        // ------------------------------------------------------------------ Messages de l'appli

        private async void OnDevMessage(DiagMessage m)
        {
            if (!_devSession) return;
            switch (m.Type)
            {
                case "FEATURES":
                    _devFeatures = DeviceFeatures.From(m);
                    foreach (var it in _devItems)
                    {
                        it.Available = _devFeatures.Has(it.Def.Feature);
                        if (!it.Available && it.Status == "⚪") it.Status = "➖";
                    }
                    string model = string.Join(" ", new[] { _devFeatures.Manufacturer, _devFeatures.Model }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    int count = _devItems.Count(i => i.Available);
                    SetDevStatus(Loc.F("Prêt : {0} (Android {1}) — {2} test(s) possible(s). Choisissez un test dans la liste.", model, _devFeatures.Android, count)
                                 + (string.IsNullOrEmpty(_devFeatures.Scanner) ? "" : " " + Loc.F("Lecteur détecté : {0}.", _devFeatures.Scanner)) + _devImportNote);
                    BtnDevTestsStart.IsEnabled = false;
                    if (DevTestsList.SelectedIndex < 0) DevTestsList.SelectedItem = _devItems.FirstOrDefault(i => i.Available);
                    RefreshDevTestPanel();
                    break;

                case "LIVE":
                    if (m.Test == _devCurrent && SelectedDevTest?.Id == m.Test)
                    {
                        TxtDevTestLive.Text = DeviceTestText.Live(m);
                        DevTestLiveBox.Visibility = Visibility.Visible;
                    }
                    break;

                case "RESULT":
                {
                    var item = _devItems.FirstOrDefault(i => i.Id == m.Test);
                    if (item == null) break;
                    if (item.ManualVerdict)
                    {
                        // Le technicien a déjà tranché (clic un peu avant la fin du test) : on garde son choix,
                        // la mesure ne sert qu'à compléter le détail s'il était vide.
                        if (string.IsNullOrWhiteSpace(item.Details) || item.Details == Loc.T("Résultat constaté par le technicien."))
                            item.Details = DeviceTestText.Confirmed(item.Id, item.Status, DeviceTestText.Result(m));
                    }
                    else
                    {
                        item.Status = DeviceTestText.StatusIcon(m.Status);
                        item.Details = DeviceTestText.Result(m);
                    }
                    if (m.Status == "na") item.Available = false;
                    string remote = m.Str("file");
                    if (!string.IsNullOrEmpty(remote)) item.LocalFile = await PullDevFileAsync(remote, item.Id);
                    if (SelectedDevTest == item) RefreshDevTestPanel();
                    RefreshDevSummary();
                    // Résultat mesuré : test suivant ; « à confirmer » : on attend le choix du technicien.
                    if (m.Status != "confirm" && !item.ManualVerdict) ChainAdvance(item.Id, m.Status == "na" ? 0.5 : 3.0);
                    break;
                }

                case "VERDICT":
                {
                    // Choix fait sur le terminal (boutons ✅ ⚠️ ❌ ⏭ de l'appli).
                    var item = _devItems.FirstOrDefault(i => i.Id == m.Test);
                    if (item == null) break;
                    string icon = DeviceTestText.StatusIcon(m.Status);
                    item.Status = icon;
                    item.ManualVerdict = icon != "⚪";
                    if (icon == "⚪") { item.Details = ""; item.LocalFile = null; }
                    else item.Details = DeviceTestText.Confirmed(item.Id, icon, item.Details);
                    if (SelectedDevTest == item) RefreshDevTestPanel();
                    RefreshDevSummary();
                    ChainAdvance(item.Id, 1.0);
                    break;
                }

                case "STATE":
                    if (m.Status == "stopped" && m.Test == _devCurrent)
                    {
                        _devCurrent = null;
                        var it = _devItems.FirstOrDefault(i => i.Id == m.Test);
                        if (it != null && it.Status == "⏳")
                        {
                            it.Status = "⚪"; // arrêté avant d'avoir un résultat
                            ChainAdvance(it.Id, 1.5);
                        }
                        RefreshDevTestPanel();
                    }
                    break;
            }
        }

        /// <summary>Photo ou enregistrement du terminal copié dans le dossier des captures.</summary>
        private async Task<string> PullDevFileAsync(string remote, string testId)
        {
            try
            {
                string dir = Path.Combine(GetCaptureFolderPath(), "Tests_terminal");
                Directory.CreateDirectory(dir);
                string local = Path.Combine(dir, $"{FileNameHelper.Sanitize(_devSerial)}_{testId}_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(remote)}");
                var res = await _adbManager.ExecuteAsync($"-s {_devSerial} pull {AdbManager.QuoteLocalArg(remote)} {AdbManager.QuoteLocalArg(local)}", TimeSpan.FromSeconds(60));
                return File.Exists(local) ? local : null;
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ Panneau du test choisi

        private void DevTestsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshDevTestPanel();

        private void RefreshDevTestPanel()
        {
            var it = SelectedDevTest;
            if (it == null)
            {
                TxtDevTestTitle.Text = Loc.T("Choisissez un test dans la liste");
                TxtDevTestInstruction.Text = _devSession ? "" : Loc.T("Commencez par « Préparer le terminal » : les tests impossibles sur ce modèle sont grisés.");
                BtnDevTestRun.IsEnabled = BtnDevTestAbort.IsEnabled = false;
                DevTestVerdictPanel.IsEnabled = false;
                DevTestLiveBox.Visibility = DevTestPhoto.Visibility = BtnDevTestOpenFile.Visibility = BtnDevTestPlay.Visibility = Visibility.Collapsed;
                TxtDevTestResult.Text = "";
                RefreshDevSummary();
                return;
            }
            TxtDevTestTitle.Text = it.Icon + "  " + it.Name;
            TxtDevTestInstruction.Text = it.Available ? Loc.T(it.Def.Instruction) : Loc.T("Ce terminal n'a pas ce matériel : le test n'apparaîtra pas dans le rapport.");
            bool running = _devCurrent != null;
            BtnDevTestRun.IsEnabled = _devSession && _devFeatures != null && it.Available && !running && !_chainActive;
            BtnDevTestsChain.IsEnabled = _devSession && _devFeatures != null && (_chainActive || !running);
            BtnDevTestRun.Content = it.Def.External ? Loc.T("▶ Ouvrir le test du lecteur") : Loc.T("▶ Lancer le test");
            BtnDevTestAbort.IsEnabled = running && _devCurrent == it.Id;
            DevTestVerdictPanel.IsEnabled = it.Available && !it.Def.External && _devCurrent != it.Id;
            if (_devCurrent != it.Id) DevTestLiveBox.Visibility = Visibility.Collapsed;

            TxtDevTestResult.Text = it.Status == "⏳" ? Loc.T("Test en cours sur le terminal…")
                                  : string.IsNullOrEmpty(it.Details) ? "" : it.Details;
            if (!string.Equals(TxtDevTestComment.Text, it.Comment)) TxtDevTestComment.Text = it.Comment ?? "";

            string file = it.LocalFile;
            bool isImage = file != null && File.Exists(file) && file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
            bool isWav = file != null && File.Exists(file) && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            DevTestPhoto.Source = isImage ? LoadBitmapNoLock(file) : null;
            DevTestPhoto.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            BtnDevTestOpenFile.Visibility = file != null && File.Exists(file) ? Visibility.Visible : Visibility.Collapsed;
            BtnDevTestPlay.Visibility = isWav ? Visibility.Visible : Visibility.Collapsed;

            // Résultat retenu : bouton correspondant mis en avant (bord épais), les autres atténués.
            foreach (var b in DevTestVerdictPanel.Children.OfType<Button>())
            {
                bool selected = (b.Tag as string) == it.Status;
                b.BorderThickness = new Thickness(selected ? 2.5 : 1);
                b.Opacity = selected || !it.IsReported ? 1.0 : 0.55;
            }
            RefreshDevSummary();
        }

        private static BitmapImage LoadBitmapNoLock(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // fichier non verrouillé
                bmp.DecodePixelWidth = 900;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private void RefreshDevSummary()
        {
            int ok = _devItems.Count(i => i.Status == "✅"), warn = _devItems.Count(i => i.Status == "⚠️"), bad = _devItems.Count(i => i.Status == "❌");
            int confirm = _devItems.Count(i => i.Status == "❓");
            int done = ok + warn + bad;
            TxtDevTestsSummary.Text = done == 0 && confirm == 0
                ? Loc.T("Aucun test fait pour l'instant.")
                : Loc.F("{0} test(s) fait(s) : {1} conforme(s), {2} à surveiller, {3} non conforme(s)", done, ok, warn, bad)
                  + (confirm > 0 ? " — " + Loc.F("{0} à confirmer", confirm) : "");
            BtnDevTestsReport.IsEnabled = done > 0 || _diagList?.Any(d => d.SystemId?.StartsWith(DeviceTestCatalog.SystemIdPrefix) == true) == true;
        }

        private async void BtnDevTestRun_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedDevTest;
            if (it == null || !_devSession || _devCurrent != null) return;
            if (it.Def.External)
            {
                BtnOpenScanTest_Click(sender, e);
                return;
            }
            await RunDevTestAsync(it, "");
        }

        /// <summary>Lance un test sur le terminal ; <paramref name="step"/> (« 3/12 ») s'affiche sur le terminal pendant un enchaînement.</summary>
        private async Task RunDevTestAsync(DeviceTestItem it, string step)
        {
            // Fonctions coupées sur le terminal : on tente de les activer (sans effet si le terminal le refuse).
            if (it.Id == "nfc") await _adbManager.ExecuteAsync($"-s {_devSerial} shell svc nfc enable");
            if (it.Id == "gps")
            {
                await _adbManager.ExecuteAsync($"-s {_devSerial} shell cmd location set-location-enabled true");
                await _adbManager.ExecuteAsync($"-s {_devSerial} shell settings put secure location_mode 3");
            }
            it.Status = "⏳";
            it.Details = "";
            it.LocalFile = null;
            it.ManualVerdict = false;
            TxtDevTestLive.Text = "";
            _devCurrent = it.Id;
            RefreshDevTestPanel();
            string args = DiagApp.CommandArgs(_devSerial, it.Id) + (string.IsNullOrEmpty(step) ? "" : " -e step " + step);
            await _adbManager.ExecuteAsync(args);
        }

        // ------------------------------------------------------------------ Enchaînement des tests

        private readonly System.Collections.Generic.Queue<string> _chainQueue = new System.Collections.Generic.Queue<string>();
        private bool _chainActive;
        private string _chainCurrent;
        private int _chainTotal, _chainIndex;
        private DispatcherTimer _chainTimer;

        private async void BtnDevTestsChain_Click(object sender, RoutedEventArgs e)
        {
            if (_chainActive) { await StopChainAsync(); return; }
            if (!_devSession || _devFeatures == null || _devCurrent != null) return;
            _chainQueue.Clear();
            foreach (var id in DeviceTestCatalog.ChainOrder)
            {
                var it = _devItems.FirstOrDefault(i => i.Id == id);
                if (it != null && it.Available && !it.Def.External && (it.Status == "⚪" || it.Status == "❓")) _chainQueue.Enqueue(id);
            }
            if (_chainQueue.Count == 0)
            {
                SetDevStatus(Loc.T("Tous les tests possibles ont déjà un résultat. Choisissez « Ne pas tester » sur un test pour le refaire."));
                return;
            }
            _chainActive = true;
            _chainTotal = _chainQueue.Count;
            _chainIndex = 0;
            BtnDevTestsChain.Content = Loc.T("⏹ Arrêter l'enchaînement");
            RunNextInChain();
        }

        private async void RunNextInChain()
        {
            if (!_chainActive || !_devSession) return;
            while (_chainQueue.Count > 0)
            {
                string nextId = _chainQueue.Dequeue(); // une seule fois (dans le prédicat, elle vidait la file)
                var it = _devItems.FirstOrDefault(i => i.Id == nextId);
                _chainIndex++;
                if (it == null || !it.Available) continue;
                _chainCurrent = it.Id;
                DevTestsList.SelectedItem = it;
                DevTestsList.ScrollIntoView(it);
                SetDevStatus(Loc.F("Enchaînement : test {0} sur {1} — {2}. Validez sur le PC ou sur le terminal (✅ ⚠️ ❌ ⏭).", _chainIndex, _chainTotal, it.Name));
                try { await RunDevTestAsync(it, $"{_chainIndex}/{_chainTotal}"); }
                catch (Exception ex) { FinishChain(Loc.F("Enchaînement arrêté : {0}", ex.Message)); }
                return;
            }
            FinishChain(Loc.F("Enchaînement terminé : {0} test(s) passés. Vérifiez les résultats puis « Reporter dans le diagnostic SAV ».", _chainTotal));
        }

        /// <summary>Le test de l'enchaînement a son résultat : on passe au suivant après un court délai (le temps de lire).</summary>
        private void ChainAdvance(string testId, double delaySeconds)
        {
            if (!_chainActive || testId != _chainCurrent) return;
            _chainCurrent = null;
            _chainTimer?.Stop();
            _chainTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySeconds) };
            _chainTimer.Tick += (s, e) => { _chainTimer.Stop(); RunNextInChain(); };
            _chainTimer.Start();
        }

        private async Task StopChainAsync()
        {
            bool running = _devCurrent != null;
            FinishChain(Loc.T("Enchaînement arrêté."));
            if (running && _devSerial != null) await SendDiagCommandAsync(_devSerial, "stop");
        }

        private void FinishChain(string message)
        {
            _chainActive = false;
            _chainCurrent = null;
            _chainQueue.Clear();
            _chainTimer?.Stop();
            BtnDevTestsChain.Content = Loc.T("⏩ Enchaîner les tests");
            SetDevStatus(message);
            RefreshDevTestPanel();
        }

        private async void BtnDevTestAbort_Click(object sender, RoutedEventArgs e)
        {
            if (_devCurrent == null || _devSerial == null) return;
            await SendDiagCommandAsync(_devSerial, "stop");
        }

        private void BtnDevTestVerdict_Click(object sender, RoutedEventArgs e)
        {
            var it = SelectedDevTest;
            if (it == null || !(sender is Button b) || !(b.Tag is string status)) return;
            it.Status = status;
            it.ManualVerdict = status != "⚪";
            if (status == "⚪") { it.Details = ""; it.LocalFile = null; }
            else it.Details = DeviceTestText.Confirmed(it.Id, status, it.Details); // la question devient un constat
            RefreshDevTestPanel();
            ChainAdvance(it.Id, 0.8);
        }

        private void TxtDevTestComment_TextChanged(object sender, TextChangedEventArgs e)
        {
            var it = SelectedDevTest;
            if (it != null) it.Comment = TxtDevTestComment.Text ?? "";
        }

        private void BtnDevTestOpenFile_Click(object sender, RoutedEventArgs e)
        {
            var f = SelectedDevTest?.LocalFile;
            if (f != null && File.Exists(f)) ShowInExplorer(f);
        }

        private void BtnDevTestPlay_Click(object sender, RoutedEventArgs e)
        {
            var f = SelectedDevTest?.LocalFile;
            if (f == null || !File.Exists(f)) return;
            try { new System.Media.SoundPlayer(f).Play(); }
            catch (Exception ex) { SetDevStatus(Loc.F("Lecture impossible : {0}", ex.Message)); }
        }

        // ------------------------------------------------------------------ Report dans le diagnostic SAV

        /// <summary>
        /// Les tests faits (✅ ⚠️ ❌) sont ajoutés à la checklist (lignes « dev_… ») ; ceux qui n'ont pas été faits
        /// en sont retirés, pour que le rapport ne montre que ce qui a été réellement testé.
        /// </summary>
        private async void BtnDevTestsReport_Click(object sender, RoutedEventArgs e)
        {
            int pending = _devItems.Count(i => i.Status == "❓");
            if (pending > 0)
            {
                bool go = await ShowCustomDialogAsync(Loc.T("🧪 Tests du terminal"),
                    Loc.F("{0} test(s) attendent votre confirmation (✅, ⚠️ ou ❌) : ils ne seront pas reportés.\n\nReporter quand même les autres ?", pending),
                    Loc.T("Reporter"), Loc.T("Annuler"), false);
                if (!go) return;
            }
            if (_diagList == null) InitializeDiagChecklist();
            if (_diagList == null) return;

            foreach (var it in _devItems.Where(i => !i.Def.External))
            {
                string sid = DeviceTestCatalog.SystemIdPrefix + it.Id;
                var existing = _diagList.FirstOrDefault(d => d.SystemId == sid);
                if (!it.IsReported)
                {
                    if (existing != null) _diagList.Remove(existing);
                    continue;
                }
                if (existing == null)
                {
                    existing = new DiagComponent { SystemId = sid, ComponentName = it.Def.Name };
                    _diagList.Add(existing);
                }
                existing.StatusIcon = it.Status;
                existing.Details = it.Details;
                existing.Comment = it.Comment ?? "";
                existing.ImagePath = it.LocalFile != null && it.LocalFile.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? it.LocalFile : "";
            }
            DiagChecklistGrid.Items.Refresh();
            int n = _devItems.Count(i => i.IsReported && !i.Def.External);
            SetDevStatus(Loc.F("{0} test(s) reporté(s) dans le diagnostic SAV (et le rapport Word).", n));
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🧪 Tests du terminal : {1} résultat(s) reporté(s) dans le diagnostic SAV.", DateTime.Now, n));
        }
    }
}
