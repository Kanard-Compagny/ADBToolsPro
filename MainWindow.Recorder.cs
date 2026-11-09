using ADBToolsNew.Core;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ADBToolsNew
{
    /// <summary>
    /// Enregistreur terrain (Analyse de fichiers ▸ « Enregistreur terrain ») : l'appli de diagnostic enregistre sur le
    /// terminal débranché le journal Android complet, l'état du terminal et les incidents signalés par l'utilisateur
    /// (DiagApp/RecorderService.java). Au retour, le dossier est copié sur le PC et ouvert dans Analyse de fichiers.
    /// </summary>
    public partial class MainWindow
    {
        private static readonly string RecorderRemoteDir = $"/sdcard/Android/data/{DiagApp.Package}/files/recordings";

        private async void BtnOpenRecorder_Click(object sender, RoutedEventArgs e)
        {
            RecorderOverlay.Visibility = Visibility.Visible;
            await FillRecorderTargetsAsync();
        }

        private void BtnRecorderClose_Click(object sender, RoutedEventArgs e) => RecorderOverlay.Visibility = Visibility.Collapsed;

        /// <summary>Applications installées par l'utilisateur ou le MDM (pm list packages -3), pour choisir l'appli métier.</summary>
        private async Task FillRecorderTargetsAsync()
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device" || CmbRecorderTarget.Items.Count > 0) return;
            try
            {
                var res = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell pm list packages -3");
                var pkgs = (res.StandardOutput ?? "").Replace("\r", "").Split('\n')
                    .Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l.Substring(8).Trim())
                    .Where(p => p.Length > 0 && p != DiagApp.Package).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                CmbRecorderTarget.ItemsSource = pkgs;
            }
            catch { /* liste facultative : la saisie libre reste possible */ }
        }

        private async void BtnRecorderStart_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🎙 Enregistreur terrain"), Loc.T("Connectez d'abord le terminal (ADB autorisé)."), Loc.T("OK"), "", false);
                return;
            }
            if (dev.Serial.Contains(":"))
            {
                await ShowCustomDialogAsync(Loc.T("🎙 Enregistreur terrain"), Loc.T("Branchez le terminal en USB pour démarrer l'enregistrement (une connexion ADB par Wi-Fi serait coupée une fois le terminal emporté)."), Loc.T("OK"), "", false);
                return;
            }
            string target = (CmbRecorderTarget.Text ?? "").Trim();
            if (target.Length > 0 && !Regex.IsMatch(target, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"))
            {
                TxtRecorderStatus.Text = Loc.T("Nom d'application invalide (ex. com.entreprise.appli).");
                return;
            }
            BtnRecorderStart.IsEnabled = false;
            try
            {
                TxtRecorderStatus.Text = Loc.T("Préparation du terminal…");
                if (!await EnsureDiagAppAsync(dev.Serial, "record")) { TxtRecorderStatus.Text = Loc.T("Installation de l'appli de diagnostic impossible."); return; }
                string s = dev.Serial, pkg = DiagApp.Package;
                // Journal complet et état du système : droits réservés au débogage, accordés ici par USB.
                await _adbManager.ExecuteAsync($"-s {s} shell pm grant {pkg} android.permission.READ_LOGS");
                await _adbManager.ExecuteAsync($"-s {s} shell pm grant {pkg} android.permission.DUMP");
                // Plusieurs heures écran éteint : pas de mise en veille profonde ni de restriction d'arrière-plan.
                await _adbManager.ExecuteAsync($"-s {s} shell dumpsys deviceidle whitelist +{pkg}");
                await _adbManager.ExecuteAsync($"-s {s} shell cmd appops set {pkg} RUN_ANY_IN_BACKGROUND allow");
                // Nom du réseau et de la borne : Android ne les donne que si la localisation est active.
                await _adbManager.ExecuteAsync($"-s {s} shell cmd location set-location-enabled true");

                int interval = ComboTag(CmbRecorderInterval, 15), duration = ComboTag(CmbRecorderDuration, 240);
                string args = $"{DiagApp.CommandArgs(s, "record")} --ei interval_s {interval} --ei duration_min {duration}" + (target.Length > 0 ? $" -e target {target}" : "");
                await _adbManager.ExecuteAsync(args);
                await Task.Delay(2500);

                var log = await _adbManager.ExecuteAsync($"-s {s} logcat -d -v brief -s {DiagApp.LogTag}:I");
                var started = (log.StandardOutput ?? "").Replace("\r", "").Split('\n').Select(DiagMessage.Parse)
                    .LastOrDefault(m => m != null && m.Type == "RESULT" && m.Test == "record");
                if (started == null || started.Status != "ok")
                {
                    TxtRecorderStatus.Text = Loc.T("Le terminal n'a pas confirmé le démarrage : déverrouillez son écran puis réessayez.");
                    return;
                }
                string until = duration > 0 ? Loc.F("pendant {0} h au plus", (duration / 60.0).ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)) : Loc.T("jusqu'à l'arrêt");
                TxtRecorderStatus.Text = Loc.F("✅ Enregistrement en cours ({0}). Vous pouvez débrancher le terminal et le confier à l'utilisateur : au moindre problème, il appuie sur « Signaler un incident ». Au retour, rebranchez-le puis « Récupérer l'enregistrement ».", until);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🎙 Enregistreur terrain démarré sur {1}.", DateTime.Now, s));
            }
            catch (Exception ex)
            {
                TxtRecorderStatus.Text = Loc.F("Démarrage impossible : {0}", ex.Message);
            }
            finally { BtnRecorderStart.IsEnabled = true; }
        }

        private async void BtnRecorderFetch_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync(Loc.T("🎙 Enregistreur terrain"), Loc.T("Rebranchez le terminal (ADB autorisé) pour récupérer l'enregistrement."), Loc.T("OK"), "", false);
                return;
            }
            BtnRecorderFetch.IsEnabled = false;
            try
            {
                string serial = dev.Serial;
                TxtRecorderStatus.Text = Loc.T("Arrêt de l'enregistrement et copie sur le PC…");
                await _adbManager.ExecuteAsync(DiagApp.CommandArgs(serial, "record_stop"));
                await Task.Delay(2000);

                var ls = await _adbManager.ExecuteAsync($"-s {serial} shell ls -1 {RecorderRemoteDir}");
                var dirs = (ls.StandardOutput ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim())
                    .Where(x => x.StartsWith("rec_", StringComparison.Ordinal) && Regex.IsMatch(x, @"^rec_[0-9_]+$")).OrderBy(x => x).ToList();
                if (dirs.Count == 0)
                {
                    TxtRecorderStatus.Text = Loc.T("Aucun enregistrement sur ce terminal (non démarré, ou déjà récupéré).");
                    return;
                }
                string root = Path.Combine(GetCaptureFolderPath(), "Enregistrements");
                Directory.CreateDirectory(root);
                string last = null;
                foreach (var d in dirs)
                {
                    string local = Path.Combine(root, $"{FileNameHelper.Sanitize(serial)}_{d}");
                    if (Directory.Exists(local)) local += "_" + DateTime.Now.ToString("HHmmss");
                    TxtRecorderStatus.Text = Loc.F("Copie de {0}…", d);
                    await _adbManager.ExecuteAsync($"-s {serial} pull {RecorderRemoteDir}/{d} {AdbManager.QuoteLocalArg(local)}", TimeSpan.FromMinutes(10));
                    if (Directory.Exists(local) && Directory.EnumerateFiles(local).Any())
                    {
                        last = local;
                        await _adbManager.ExecuteAsync($"-s {serial} shell rm -r {RecorderRemoteDir}/{d}"); // copié : retiré du terminal
                    }
                }
                if (last == null) { TxtRecorderStatus.Text = Loc.T("Copie de l'enregistrement impossible."); return; }

                _diagAppUsers.Add("record");
                if (_diagAppSerial == null) _diagAppSerial = serial;
                await ReleaseDiagAppAsync("record");

                TxtRecorderStatus.Text = Loc.F("✅ {0} enregistrement(s) copié(s) dans {1}.", dirs.Count, root);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🎙 Enregistrement récupéré : {1}", DateTime.Now, last));
                RecorderOverlay.Visibility = Visibility.Collapsed;
                NavFileAnalysis_Click(null, null);
                await LoadFileAnalysisAsync(last);
                GoToFirstIncident();
            }
            catch (Exception ex)
            {
                TxtRecorderStatus.Text = Loc.F("Récupération impossible : {0}", ex.Message);
            }
            finally { BtnRecorderFetch.IsEnabled = true; }
        }

        private async void BtnRecorderOpen_Click(object sender, RoutedEventArgs e)
        {
            string path = FolderPicker.Pick(this, Loc.T("Dossier d'enregistrement (rec_…)"), Path.Combine(GetCaptureFolderPath(), "Enregistrements"));
            if (path == null) return;
            RecorderOverlay.Visibility = Visibility.Collapsed;
            NavFileAnalysis_Click(null, null);
            await LoadFileAnalysisAsync(path);
            GoToFirstIncident();
        }

        /// <summary>Après ouverture d'un enregistrement : placé sur le premier incident signalé, s'il y en a.</summary>
        private void GoToFirstIncident()
        {
            if (_faArchive == null) return;
            var first = _faArchive.Entries.FirstOrDefault(x => x.Tag == LogArchive.IncidentTag);
            if (first != null) FaGoToEntry(first);
        }

        /// <summary>Un enregistrement non récupéré est encore sur le terminal (l'appli ne doit pas être désinstallée).</summary>
        private async Task<bool> HasPendingRecordingAsync(string serial)
        {
            try
            {
                var ls = await _adbManager.ExecuteAsync($"-s {serial} shell ls -1 {RecorderRemoteDir}");
                return (ls.StandardOutput ?? "").Contains("rec_");
            }
            catch { return false; }
        }
    }
}
