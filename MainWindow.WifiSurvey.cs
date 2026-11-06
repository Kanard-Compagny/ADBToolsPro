using ADBToolsNew.Core;
using LiveCharts;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ADBToolsNew
{
    /// <summary>
    /// Mesure Wi-Fi sans câble (Analyse Wi-Fi ▸ « Mesure sans câble ») : l'appli de diagnostic mesure le Wi-Fi sur le
    /// terminal débranché (DiagApp/SurveyService.java) ; au retour, le relevé est copié sur le PC, affiché dans
    /// l'Analyse Wi-Fi (jauges, statistiques, journal avec bilan par repère) et repris par le rapport Word.
    /// </summary>
    public partial class MainWindow
    {
        private WifiSurvey _importedSurvey;
        private static readonly string SurveyRemoteDir = $"/sdcard/Android/data/{DiagApp.Package}/files/surveys";

        private void BtnOpenWifiSurvey_Click(object sender, RoutedEventArgs e)
        {
            WifiSurveyOverlay.Visibility = Visibility.Visible;
        }

        private void BtnWifiSurveyClose_Click(object sender, RoutedEventArgs e)
        {
            WifiSurveyOverlay.Visibility = Visibility.Collapsed;
        }

        private static int ComboTag(ComboBox cb, int def) =>
            cb.SelectedItem is ComboBoxItem it && int.TryParse(it.Tag as string, out int v) ? v : def;

        private async void BtnSurveyStart_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🚶 Mesure sans câble"), Loc.T("Connectez d'abord le terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            if (dev.Serial.Contains(":"))
            {
                await ShowCustomDialogAsync(Loc.T("🚶 Mesure sans câble"), Loc.T("Branchez le terminal en USB pour démarrer la mesure (une connexion ADB par Wi-Fi serait coupée pendant le parcours)."), Loc.T("OK"), "", false);
                return;
            }
            BtnSurveyStart.IsEnabled = false;
            try
            {
                TxtSurveyStatus.Text = Loc.T("Préparation du terminal…");
                if (!await EnsureDiagAppAsync(dev.Serial, "survey")) { TxtSurveyStatus.Text = Loc.T("Installation de l'appli de diagnostic impossible."); return; }
                // Mesure écran éteint : l'appli est exemptée de la mise en veille profonde (Doze) et des
                // restrictions d'arrière-plan, sinon Android espace ou coupe les mesures au bout de quelques minutes.
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell dumpsys deviceidle whitelist +{DiagApp.Package}");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd appops set {DiagApp.Package} RUN_ANY_IN_BACKGROUND allow");
                // Nom du réseau et de la borne : Android ne les donne que si la localisation est active.
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd location set-location-enabled true");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put secure location_mode 3");

                string target = (TxtSurveyTarget.Text ?? "").Trim();
                if (target.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(target, @"^[A-Za-z0-9.\-:]+$"))
                {
                    TxtSurveyStatus.Text = Loc.T("Cible du ping invalide : adresse IP ou nom de serveur uniquement.");
                    return;
                }
                int interval = ComboTag(CmbSurveyInterval, 1000), duration = ComboTag(CmbSurveyDuration, 0);
                string args = $"{DiagApp.CommandArgs(dev.Serial, "survey")} --ei interval_ms {interval} --ei duration_min {duration}" + (target.Length > 0 ? $" -e target {target}" : "");
                await _adbManager.ExecuteAsync(args);
                await Task.Delay(2500);

                // Confirmation de l'appli : « RESULT survey ok » dans le journal.
                var log = await _adbManager.ExecuteAsync($"-s {dev.Serial} logcat -d -v brief -s {DiagApp.LogTag}:I");
                var started = (log.StandardOutput ?? "").Replace("\r", "").Split('\n').Select(DiagMessage.Parse)
                    .LastOrDefault(m => m != null && m.Type == "RESULT" && m.Test == "survey");
                if (started == null || started.Status != "ok")
                {
                    TxtSurveyStatus.Text = Loc.T("Le terminal n'a pas confirmé le démarrage : déverrouillez son écran puis réessayez.");
                    return;
                }
                string tgt = started.Str("target");
                TxtSurveyStatus.Text = Loc.F("✅ Mesure en cours (ping vers {0}). Vous pouvez débrancher le terminal et parcourir le site ; posez des repères sur le terminal. Au retour, rebranchez-le puis « Récupérer les mesures ».",
                    string.IsNullOrEmpty(tgt) ? "?" : tgt);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🚶 Mesure Wi-Fi sans câble démarrée sur {1}.", DateTime.Now, dev.Serial));
            }
            catch (Exception ex)
            {
                TxtSurveyStatus.Text = Loc.F("Démarrage impossible : {0}", ex.Message);
            }
            finally { BtnSurveyStart.IsEnabled = true; }
        }

        private async void BtnSurveyFetch_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🚶 Mesure sans câble"), Loc.T("Rebranchez le terminal (ADB autorisé) pour récupérer les mesures."), Loc.T("OK"), "", false);
                return;
            }
            BtnSurveyFetch.IsEnabled = false;
            try
            {
                string serial = dev.Serial;
                TxtSurveyStatus.Text = Loc.T("Arrêt de la mesure et récupération du relevé…");
                await _adbManager.ExecuteAsync(DiagApp.CommandArgs(serial, "survey_stop"));
                await Task.Delay(1500);

                var ls = await _adbManager.ExecuteAsync($"-s {serial} shell ls -1 {SurveyRemoteDir}");
                var files = (ls.StandardOutput ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim())
                    .Where(x => x.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x).ToList();
                if (files.Count == 0)
                {
                    TxtSurveyStatus.Text = Loc.T("Aucun relevé sur ce terminal (mesure non démarrée, ou déjà récupérée).");
                    return;
                }
                string dir = Path.Combine(GetCaptureFolderPath(), "Mesures_WiFi");
                Directory.CreateDirectory(dir);
                string last = null;
                foreach (var f in files)
                {
                    string local = Path.Combine(dir, $"{FileNameHelper.Sanitize(serial)}_{f}");
                    await _adbManager.ExecuteAsync($"-s {serial} pull {SurveyRemoteDir}/{f} {AdbManager.QuoteLocalArg(local)}", TimeSpan.FromSeconds(120));
                    if (File.Exists(local))
                    {
                        last = local;
                        await _adbManager.ExecuteAsync($"-s {serial} shell rm {SurveyRemoteDir}/{f}"); // copié : retiré du terminal
                    }
                }
                if (last == null) { TxtSurveyStatus.Text = Loc.T("Copie du relevé impossible."); return; }

                // Relevés récupérés : l'appli peut être retirée (sauf si une autre fenêtre s'en sert encore).
                _diagAppUsers.Add("survey");
                if (_diagAppSerial == null) _diagAppSerial = serial;
                await ReleaseDiagAppAsync("survey");

                var survey = WifiSurvey.Load(last);
                ImportWifiSurvey(survey, serial);
                TxtSurveyStatus.Text = Loc.F("✅ {0} relevé(s) copié(s) dans {1}. Le plus récent est affiché dans l'Analyse Wi-Fi.", files.Count, dir);
                WifiSurveyOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                TxtSurveyStatus.Text = Loc.F("Récupération impossible : {0}", ex.Message);
            }
            finally { BtnSurveyFetch.IsEnabled = true; }
        }

        private void BtnSurveyOpenFile_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(GetCaptureFolderPath(), "Mesures_WiFi");
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.T("Ouvrir un relevé Wi-Fi"),
                Filter = Loc.T("Relevé Wi-Fi ADB Tools (*.jsonl)|*.jsonl|Tous les fichiers (*.*)|*.*"),
                InitialDirectory = Directory.Exists(dir) ? dir : "",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var survey = WifiSurvey.Load(dlg.FileName);
                if (survey.Samples.Count == 0) { TxtSurveyStatus.Text = Loc.T("Ce fichier ne contient aucune mesure."); return; }
                string name = Path.GetFileNameWithoutExtension(dlg.FileName);
                int cut = name.IndexOf("_survey_", StringComparison.Ordinal);
                ImportWifiSurvey(survey, cut > 0 ? name.Substring(0, cut) : survey.Model);
                WifiSurveyOverlay.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) { TxtSurveyStatus.Text = Loc.F("Lecture impossible : {0}", ex.Message); }
        }

        /// <summary>Affiche le relevé dans l'Analyse Wi-Fi, comme une session terminée (rapports compris).</summary>
        private void ImportWifiSurvey(WifiSurvey s, string serial)
        {
            if (_radarService.IsRunning) StopRadar();
            NavRadar_Click(null, null);
            _importedSurvey = s;
            _radarSerial = serial;

            _pingHistory.Clear();
            _pingHistory.AddRange(s.PingHistory);
            _rssiHistory.Clear();
            _rssiHistory.AddRange(s.RssiHistory);

            PingValues = new ChartValues<int>(_pingHistory.Skip(Math.Max(0, _pingHistory.Count - 40)).Select(v => v ?? 0));
            PingLineSeries.Values = PingValues;
            RssiValues = new ChartValues<int>(_rssiHistory.Skip(Math.Max(0, _rssiHistory.Count - 40)));
            RssiLineSeries.Values = RssiValues;
            if (PingChart.AxisY.Count > 0)
            {
                int peak = PingValues.Count > 0 ? PingValues.Max() : 0;
                PingChart.AxisY[0].MaxValue = Math.Max(200, Math.Ceiling(peak * 1.1 / 50.0) * 50);
            }

            var last = s.Last;
            var st = s.Stats;
            TxtWifiSsid.Text = last?.Ssid ?? Loc.T("Déconnecté");
            TxtWifiBssid.Text = Loc.F("BSSID : {0}", last?.Bssid ?? "--");
            TxtWifiRssi.Text = Loc.F("{0} dBm", st.AverageRssi);
            TxtWifiRssi.Foreground = st.AverageRssi >= -65 ? BrushSuccess : st.AverageRssi >= -75 ? BrushWarning : BrushDanger;
            TxtWifiSpeed.Text = last != null ? Loc.F("{0} Mbps", last.LinkMbps) : Loc.T("- Mbps");
            TxtWifiFreq.Text = last != null ? Loc.F("Bande : {0} / Canal : {1}", WifiSurvey.BandText(last.Frequency), WifiSurvey.Channel(last.Frequency)) : Loc.T("Bande : -- / Canal : --");
            TxtWifiIp.Text = $"IP : {(string.IsNullOrEmpty(s.Ip) ? "---.---.---.---" : s.Ip)}";
            TxtWifiGateway.Text = Loc.F("Passerelle : {0}", string.IsNullOrEmpty(s.Gateway) ? "---.---.---.---" : s.Gateway);
            TxtWifiDns.Text = Loc.F("DNS : {0}", string.IsNullOrEmpty(s.Dns) ? "---.---.---.---" : s.Dns);
            TxtWifiMac.Text = Loc.T("MAC : --:--:--:--:--:--");

            TxtPingCount.Text = Loc.F("Envoyés : {0}", st.PingTotal);
            TxtPacketLoss.Text = Loc.F("Perte : {0} %", st.LossPercent);
            TxtPacketLoss.Foreground = st.LossPercent > 5 ? BrushDanger : BrushSuccess;
            TxtPingLatency.Text = Loc.F("Latence : {0} ms", st.AveragePingMs);
            TxtPingLatency.Foreground = st.AveragePingMs <= 50 ? BrushSuccess : st.AveragePingMs <= 100 ? BrushWarning : BrushDanger;
            TxtPingMinMaxAvg.Text = Loc.F("Min/Moy/Max : {0} / {1} / {2} ms", st.MinPingText, st.AveragePingMs, st.MaxPingText);
            TxtRssiMinMaxAvg.Text = Loc.F("Min/Moy/Max : {0} / {1} / {2} dBm", st.MinRssiText, st.AverageRssi, st.MaxRssiText);
            TxtWifiLogcat.Text = s.JournalText();

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🚶 Mesure sans câble importée : {1} mesure(s), {2} repère(s), {3}.", DateTime.Now,
                s.Samples.Count, s.Marks.Count, WifiReportService.BuildSummaryText(st, st.Duration)));
        }

        /// <summary>Relevé encore présent sur le terminal : l'appli ne doit pas être désinstallée (il serait perdu).</summary>
        private async Task<bool> HasPendingSurveyAsync(string serial)
        {
            try
            {
                var ls = await _adbManager.ExecuteAsync($"-s {serial} shell ls -1 {SurveyRemoteDir}");
                return (ls.StandardOutput ?? "").Contains(".jsonl");
            }
            catch { return false; }
        }
    }
}
