using ADBToolsNew.Core;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ADBToolsNew
{
    /// <summary>
    /// Test du lecteur code-barres (Diagnostics SAV ▸ Laboratoire ▸ « Test du lecteur ») : ADB Tools installe une
    /// petite appli de test sur le terminal (ScanTest\ADBToolsScan.apk, compilée avec le programme), lit les
    /// lectures dans le journal Android et la gâchette avec getevent, puis désinstalle l'appli à la fin.
    /// </summary>
    public partial class MainWindow
    {
        private const string ScanAppPackage = "fr.kanard.adbtools.scan";
        private readonly AdbStreamProcess _scanLogProcess = new AdbStreamProcess();
        private readonly AdbStreamProcess _scanTriggerProcess = new AdbStreamProcess();
        private ScanTestSession _scanSession;
        private DispatcherTimer _scanStatsTimer;
        private string _scanSerial;
        private bool _scanRunning;
        private bool _scanTestBusy;
        private long _scanDeviceStartMs; // heure du terminal au démarrage : les lectures plus anciennes du journal sont ignorées

        private static string ScanAppApkPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScanTest", "ADBToolsScan.apk");

        private void BtnOpenScanTest_Click(object sender, RoutedEventArgs e)
        {
            ScanTestOverlay.Visibility = Visibility.Visible;
            if (_scanSession == null) RefreshScanTestUi();
        }

        private async void BtnScanTestClose_Click(object sender, RoutedEventArgs e)
        {
            if (_scanRunning) await StopScanTestAsync(uninstall: true);
            ScanTestOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnScanTestStart_Click(object sender, RoutedEventArgs e)
        {
            if (_scanRunning || _scanTestBusy) return;
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🔫 Test du lecteur"), Loc.T("Connectez d'abord un terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            if (!File.Exists(ScanAppApkPath))
            {
                await ShowCustomDialogAsync(Loc.T("🔫 Test du lecteur"),
                    Loc.F("L'appli de test est introuvable :\n{0}\n\nElle est fournie avec les versions publiées d'ADB Tools (dossier ScanTest).", ScanAppApkPath), Loc.T("OK"), "", false);
                return;
            }

            _scanTestBusy = true;
            BtnScanTestStart.IsEnabled = false;
            string serial = dev.Serial;
            try
            {
                SetScanStatus(Loc.T("Installation de l'appli de test sur le terminal…"));
                if (!await InstallScanAppAsync(serial)) return;

                _scanSerial = serial;
                var clock = await _adbManager.ExecuteAsync($"-s {serial} shell date +%s");
                _scanDeviceStartMs = long.TryParse((clock.StandardOutput ?? "").Trim(), out long sec) ? sec * 1000 - 2000 : 0;
                _scanSession = new ScanTestSession { Device = serial };
                _scanSession.SetExpected(ScanText.ParseExpected(TxtScanExpected.Text));
                ScanReadsGrid.ItemsSource = null;
                RememberDiagnosedDevice(serial);

                // Journal : seulement les lignes de l'appli de test, à partir de maintenant (-T 1).
                _scanLogProcess.Start(_adbExePath, $"-s {serial} logcat -T 1 -v brief -s {ScanText.LogTag}:I", line =>
                {
                    DateTime at = DateTime.Now;
                    Dispatcher.InvokeAsync(() => OnScanLogLine(line, at), DispatcherPriority.Background);
                });
                // Gâchette : appuis détectés par getevent (délai jusqu'à la lecture, lectures ratées).
                _scanTriggerProcess.Start(_adbExePath, $"-s {serial} shell getevent -l", line =>
                {
                    var r = GetEventParser.Parse((line ?? "").Trim());
                    if (r.Kind != GetEventKind.KeyPress || !ScanTriggerKeys.IsScanTrigger(r.RawKeyName)) return;
                    DateTime at = DateTime.Now;
                    Dispatcher.InvokeAsync(() => { if (_scanRunning) { _scanSession?.AddTrigger(at); RefreshScanTestUi(); } }, DispatcherPriority.Background);
                });

                await _adbManager.ExecuteAsync($"-s {serial} shell am start -n {ScanAppPackage}/.MainActivity");
                _scanRunning = true;
                _scanStatsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _scanStatsTimer.Tick += (s, ev) => RefreshScanTestUi();
                _scanStatsTimer.Start();
                BtnScanTestStop.IsEnabled = true;
                SetScanStatus(Loc.F("Test en cours sur {0} : scannez des codes avec le terminal.", serial));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔫 Test du lecteur démarré sur {1}.", DateTime.Now, serial));
                RefreshScanTestUi();
            }
            catch (Exception ex)
            {
                SetScanStatus(Loc.F("Démarrage impossible : {0}", ex.Message));
                await StopScanTestAsync(uninstall: true);
            }
            finally
            {
                _scanTestBusy = false;
                BtnScanTestStart.IsEnabled = !_scanRunning;
            }
        }

        /// <summary>Installe l'appli de test ; si une ancienne version signée autrement gêne, la désinstalle d'abord.</summary>
        private async Task<bool> InstallScanAppAsync(string serial)
        {
            string apk = AdbManager.QuoteLocalArg(ScanAppApkPath);
            var res = await _adbManager.ExecuteAsync($"-s {serial} install -r -t {apk}", TimeSpan.FromSeconds(90));
            if (!(res.StandardOutput ?? "").Contains("Success"))
            {
                await _adbManager.ExecuteAsync($"-s {serial} uninstall {ScanAppPackage}");
                res = await _adbManager.ExecuteAsync($"-s {serial} install -t {apk}", TimeSpan.FromSeconds(90));
            }
            if ((res.StandardOutput ?? "").Contains("Success")) return true;
            string detail = ((res.StandardError ?? "") + " " + (res.StandardOutput ?? "")).Trim();
            SetScanStatus(Loc.F("Installation de l'appli de test impossible : {0}", detail));
            await ShowCustomDialogAsync(Loc.T("🔫 Test du lecteur"),
                Loc.F("L'appli de test n'a pas pu être installée sur le terminal :\n{0}\n\nCertains terminaux gérés (MDM) interdisent l'installation d'applications par ADB.", PackageManagerService.DescribeInstallFailure(detail)),
                Loc.T("OK"), "", false);
            return false;
        }

        private void OnScanLogLine(string line, DateTime at)
        {
            if (!_scanRunning || _scanSession == null || string.IsNullOrEmpty(line)) return;
            if (line.Contains("READY\t"))
            {
                SetScanStatus(Loc.F("Prêt : scannez des codes avec le terminal {0}.", _scanSerial));
                return;
            }
            var read = ScanText.ParseLogLine(line, at);
            if (read == null || (read.DeviceMs > 0 && read.DeviceMs < _scanDeviceStartMs)) return; // ligne d'un test précédent
            _scanSession.AddRead(read);
            ScanReadsGrid.ItemsSource = null;
            ScanReadsGrid.ItemsSource = _scanSession.Reads;
            ScanReadsGrid.ScrollIntoView(read);
            RefreshScanTestUi();
        }

        private async void BtnScanTestStop_Click(object sender, RoutedEventArgs e) => await StopScanTestAsync(uninstall: true);

        /// <summary>Arrête le test : flux coupés, appli de test fermée puis désinstallée, profil DataWedge retiré.</summary>
        private async Task StopScanTestAsync(bool uninstall)
        {
            bool wasRunning = _scanRunning;
            _scanRunning = false;
            _scanStatsTimer?.Stop();
            _scanStatsTimer = null;
            try { _scanLogProcess.Stop(); } catch { }
            try { _scanTriggerProcess.Stop(); } catch { }
            BtnScanTestStop.IsEnabled = false;
            BtnScanTestStart.IsEnabled = true;

            string serial = _scanSerial;
            if (uninstall && !string.IsNullOrEmpty(serial))
            {
                SetScanStatus(Loc.T("Fin du test : désinstallation de l'appli de test…"));
                try
                {
                    await _adbManager.ExecuteAsync($"-s {serial} shell am force-stop {ScanAppPackage}");
                    await _adbManager.ExecuteAsync($"-s {serial} uninstall {ScanAppPackage}");
                    await _adbManager.ExecuteAsync($"-s {serial} shell am broadcast -a com.symbol.datawedge.api.ACTION --es com.symbol.datawedge.api.DELETE_PROFILE ADBToolsScan");
                }
                catch { /* terminal débranché entre-temps : rien d'autre à faire */ }
            }
            RefreshScanTestUi();
            if (wasRunning)
            {
                SetScanStatus(Loc.F("Test terminé : {0} lecture(s). L'appli de test a été retirée du terminal.", _scanSession?.Reads.Count ?? 0));
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔫 Test du lecteur terminé : {1} lecture(s).", DateTime.Now, _scanSession?.Reads.Count ?? 0));
            }
        }

        /// <summary>Fermeture d'ADB Tools : flux coupés (l'appli de test sera retirée au prochain test).</summary>
        private void StopScanTestOnExit()
        {
            _scanRunning = false;
            try { _scanLogProcess.Stop(); } catch { }
            try { _scanTriggerProcess.Stop(); } catch { }
        }

        private void SetScanStatus(string text)
        {
            if (TxtScanTestStatus != null) TxtScanTestStatus.Text = text;
        }

        private void RefreshScanTestUi()
        {
            var s = _scanSession;
            var now = DateTime.Now;
            bool has = s != null && s.Reads.Count > 0;
            TxtScanStatReads.Text = (s?.Reads.Count ?? 0).ToString();
            TxtScanStatRate.Text = s?.ReadsPerMinute is double rpm ? rpm.ToString("0.0") : "—";
            var rate = s?.SuccessRate(now);
            TxtScanStatSuccess.Text = rate.HasValue ? Loc.F("{0:0} %", rate.Value) : "—";
            TxtScanStatTriggers.Text = s != null && s.TriggerCount > 0 ? Loc.F("{0} appui(s), {1} sans lecture", s.TriggerCount, s.MissedTriggers(now)) : "";
            TxtScanStatDelay.Text = s?.AverageDelayMs is double d ? Loc.F("{0:0} ms", d) : "—";
            TxtScanStatExpected.Text = s != null && s.ExpectedCodes.Count > 0 ? $"{s.ExpectedFound} / {s.ExpectedCodes.Count}" : "—";
            BtnScanReport.IsEnabled = BtnScanCopy.IsEnabled = BtnScanExport.IsEnabled = has || (s != null && s.TriggerCount > 0);
        }

        private void TxtScanExpected_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_scanSession == null) return;
            _scanSession.SetExpected(ScanText.ParseExpected(TxtScanExpected.Text));
            ScanReadsGrid.Items.Refresh();
            RefreshScanTestUi();
        }

        private void BtnScanUseReadsAsExpected_Click(object sender, RoutedEventArgs e)
        {
            if (_scanSession == null || _scanSession.Reads.Count == 0) return;
            TxtScanExpected.Text = string.Join(Environment.NewLine,
                _scanSession.Reads.Select(r => (r.Data ?? "").TrimEnd('\r', '\n', '\t')).Where(x => x.Length > 0).Distinct());
        }

        private void BtnScanClear_Click(object sender, RoutedEventArgs e)
        {
            if (_scanSession == null) return;
            var fresh = new ScanTestSession { Device = _scanSession.Device };
            fresh.SetExpected(ScanText.ParseExpected(TxtScanExpected.Text));
            _scanSession = fresh;
            ScanReadsGrid.ItemsSource = null;
            RefreshScanTestUi();
        }

        /// <summary>« Optique Scanner » de la checklist SAV renseigné avec le résultat (et donc dans le rapport Word).</summary>
        private void BtnScanReport_Click(object sender, RoutedEventArgs e)
        {
            if (_scanSession == null) return;
            if (_diagList == null) InitializeDiagChecklist();
            var item = _diagList?.FirstOrDefault(d => d.SystemId == "scanner");
            if (item == null) return;
            var (icon, details, comment) = _scanSession.Verdict(DateTime.Now);
            item.StatusIcon = icon;
            item.Details = details;
            if (string.IsNullOrWhiteSpace(item.Comment)) item.Comment = comment;
            DiagChecklistGrid.Items.Refresh();
            SetScanStatus(Loc.F("Résultat reporté dans le diagnostic SAV : {0} {1}", icon, comment));
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔫 Test du lecteur reporté dans le diagnostic SAV : {1}", DateTime.Now, comment));
        }

        /// <summary>Résultat du test du lecteur déjà reporté : l'écoute du Laboratoire ne doit pas l'écraser.</summary>
        private static bool IsScanTestDetails(string details) =>
            !string.IsNullOrEmpty(details) && details.StartsWith(Loc.T("--- TEST DU LECTEUR (appli de test ADB Tools) ---"), StringComparison.Ordinal);

        private void BtnScanCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_scanSession == null) return;
            var (icon, details, comment) = _scanSession.Verdict(DateTime.Now);
            string list = string.Join(Environment.NewLine, _scanSession.Reads.Select(r => $"{r.Index}. {r.TimeText}  {r.DataDisplay}  [{r.Symbology}]  {r.CheckIcon}"));
            try
            {
                Clipboard.SetText(details + Environment.NewLine + icon + " " + comment + Environment.NewLine + Environment.NewLine + list);
                SetScanStatus(Loc.T("Résultat copié dans le presse-papier."));
            }
            catch (Exception ex) { SetScanStatus(Loc.F("Copie impossible : {0}", ex.Message)); }
        }

        private void BtnScanExport_Click(object sender, RoutedEventArgs e)
        {
            if (_scanSession == null) return;
            string path = Path.Combine(GetCaptureFolderPath(), $"TestLecteur_{FileNameHelper.Sanitize(_scanSession.Device)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            try
            {
                File.WriteAllText(path, _scanSession.ToCsv(), new System.Text.UTF8Encoding(true));
                SetScanStatus(Loc.F("Lectures exportées : {0}", path));
                ShowInExplorer(path);
            }
            catch (Exception ex) { SetScanStatus(Loc.F("Export impossible : {0}", ex.Message)); }
        }
    }
}
