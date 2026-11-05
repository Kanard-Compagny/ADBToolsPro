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
        // === ONGLET 5 : ADVANCED TOOLS (N2/N3) ====
        // ==========================================

        private async System.Threading.Tasks.Task RefreshToolsStateAsync()
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            try
            {
                // Toutes les valeurs en UN SEUL appel adb (auparavant 8 appels successifs à chaque ouverture de l'onglet).
                var s = await _adbManager.RunShellSectionsAsync(dev.Serial, null,
                    "settings get global wifi_on",
                    "settings get global airplane_mode_on",
                    "settings get global bluetooth_on",
                    "settings get system accelerometer_rotation",
                    "cmd uimode night",
                    "settings get system screen_brightness",
                    "wm density",
                    "wm size");

                AdbCommandResult Section(string output) => new AdbCommandResult { StandardOutput = output ?? "" };
                var resWifi = Section(s[0]);
                var resAir = Section(s[1]);
                var resBt = Section(s[2]);
                var resRot = Section(s[3]);
                var resDark = Section(s[4]);
                var resBright = Section(s[5]);
                var resDpi = Section(s[6]);
                var resSize = Section(s[7]);

                // OBLIGATOIRE : On demande la permission de mettre à jour l'interface graphique (Thread UI)
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ToggleWifi.IsChecked = (resWifi.StandardOutput.Trim() == "1");
                    ToggleAirplane.IsChecked = (resAir.StandardOutput.Trim() == "1");
                    ToggleBluetooth.IsChecked = (resBt.StandardOutput.Trim() == "1");
                    ToggleRotation.IsChecked = (resRot.StandardOutput.Trim() == "1");
                    ToggleDarkMode.IsChecked = resDark.StandardOutput.Contains("yes");

                    if (int.TryParse(resBright.StandardOutput.Trim(), out int b))
                    {
                        // Conversion de la valeur 0-255 en pourcentage 0-100 pour l'affichage
                        int percent = (int)Math.Round(b / 2.55);
                        TxtBrightness.Text = percent.ToString();
                    }

                    // Extraction dynamique du DPI : "wm density" liste la densité physique PUIS, si un override est
                    // actif, une ligne "Override density:" distincte. Une regex non ancrée sur "density:" tombait
                    // toujours sur la première occurrence (la physique) et ignorait donc un override en cours.
                    string dpiOutput = resDpi.StandardOutput ?? "";
                    var matchOverrideDpi = System.Text.RegularExpressions.Regex.Match(dpiOutput, @"Override density:\s*(\d+)");
                    var matchPhysicalDpi = System.Text.RegularExpressions.Regex.Match(dpiOutput, @"Physical density:\s*(\d+)");
                    if (matchOverrideDpi.Success) TxtDpiValue.Text = Loc.F("{0} (personnalisé)", matchOverrideDpi.Groups[1].Value);
                    else if (matchPhysicalDpi.Success) TxtDpiValue.Text = matchPhysicalDpi.Groups[1].Value;

                    // Extraction dynamique de la Résolution
                    var matchSize = System.Text.RegularExpressions.Regex.Match(resSize.StandardOutput, @"size:\s*(\d+x\d+)");
                    if (matchSize.Success) TxtResValue.Text = matchSize.Groups[1].Value;
                });
            }
            catch { /* Ignore en cas d'erreur de communication */ }
        }

        /// <summary>
        /// Rafraîchit l'état affiché (connectivité, luminosité, DPI, résolution) sans polling automatique :
        /// utile si le technicien a changé quelque chose directement sur le terminal pendant que l'onglet
        /// était déjà ouvert.
        /// </summary>
        private async void BtnRefreshTools_Click(object sender, RoutedEventArgs e) => await RefreshToolsStateAsync();

        // --- 1. SIMULATION ET CONTRÔLE ---
        private async void BtnKey_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            if (sender is Button btn && btn.Tag != null)
            {
                string keycode = btn.Tag.ToString();
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🕹️ Envoi de la touche : {1} (Key: {2})", DateTime.Now, btn.Content.ToString().Replace(" ", ""), keycode));
                try
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell input keyevent {keycode}");
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur envoi touche : {1}", DateTime.Now, ex.Message));
                }
            }
        }

        private async void ToggleRotation_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleRotation.IsChecked = !ToggleRotation.IsChecked; return; }

            string val = ToggleRotation.IsChecked == true ? "1" : "0";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔄 Rotation Auto réglée sur : {1}", DateTime.Now, (val == "1" ? "ON" : Loc.T("OFF"))));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system accelerometer_rotation {val}");
            }
            catch (Exception ex)
            {
                ToggleRotation.IsChecked = !ToggleRotation.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Rotation Auto : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void ToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleDarkMode.IsChecked = !ToggleDarkMode.IsChecked; return; }

            string mode = ToggleDarkMode.IsChecked == true ? "yes" : "no";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🌙 Bascule Mode Sombre : {1}", DateTime.Now, mode.ToUpper()));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd uimode night {mode}");
            }
            catch (Exception ex)
            {
                ToggleDarkMode.IsChecked = !ToggleDarkMode.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Mode Sombre : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- 2. AFFICHAGE & INTERFACE ---

        /// <summary>Applique la luminosité (0-100%) : appelée par les boutons +/- ET par la validation Entrée.</summary>
        private async Task ApplyBrightnessAsync(int percent)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            percent = Math.Max(0, Math.Min(100, percent));
            int brightness = (int)(percent * 2.55); // Map le pourcentage (0-100) vers la valeur système (0-255)

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💡 Luminosité réglée à {1}%", DateTime.Now, percent));

            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system screen_brightness_mode 0"); // désactive la luminosité adaptative
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system screen_brightness {brightness}");
                // "cmd display set-brightness" attend une valeur entre 0 et 1 (et non 0-255 : la valeur était
                // refusée). "2>/dev/null" ignore l'erreur si la commande n'existe pas sur les terminaux plus anciens.
                string brightnessRatio = (percent / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd display set-brightness {brightnessRatio} 2>/dev/null");

                TxtBrightness.Text = percent.ToString();
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur luminosité : {1}", DateTime.Now, ex.Message));
            }
        }

        private int ParseBrightnessOrDefault() =>
            int.TryParse(TxtBrightness.Text.Replace("%", "").Trim(), out int percent) ? percent : 50;

        private async void BtnBrightnessMinus_Click(object sender, RoutedEventArgs e) => await ApplyBrightnessAsync(ParseBrightnessOrDefault() - 5);
        private async void BtnBrightnessPlus_Click(object sender, RoutedEventArgs e) => await ApplyBrightnessAsync(ParseBrightnessOrDefault() + 5);

        private async void TxtBrightness_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            string cleanInput = TxtBrightness.Text.Replace("%", "").Trim();
            if (int.TryParse(cleanInput, out int percent)) await ApplyBrightnessAsync(percent);
            else AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez entrer un nombre valide pour la luminosité (ex: 50).", DateTime.Now));
        }

        /// <summary>Applique la densité DPI : appelée par les boutons +/- ET par la validation Entrée.</summary>
        private async Task ApplyDpiAsync(int dpi)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            dpi = Math.Max(120, dpi); // en dessous, l'interface devient inutilisable sur la plupart des terminaux
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🖥️ Changement DPI vers {1}...", DateTime.Now, dpi));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell wm density {dpi}");
                TxtDpiValue.Text = dpi.ToString();
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur DPI : {1}", DateTime.Now, ex.Message));
            }
        }

        // Lit le nombre en tête du champ : après lecture d'une densité personnalisée, le champ affiche
        // "420 (personnalisé)" et int.TryParse échouait, si bien que +/- repartaient de 240 au lieu de 420.
        private int ParseDpiOrDefault()
        {
            var m = System.Text.RegularExpressions.Regex.Match(TxtDpiValue.Text ?? "", @"^\s*(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int dpi) ? dpi : 240;
        }

        private async void BtnDpiMinus_Click(object sender, RoutedEventArgs e) => await ApplyDpiAsync(ParseDpiOrDefault() - 10);
        private async void BtnDpiPlus_Click(object sender, RoutedEventArgs e) => await ApplyDpiAsync(ParseDpiOrDefault() + 10);

        private async void TxtDpiValue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            int dpi = ParseDpiOrDefault();
            if (System.Text.RegularExpressions.Regex.IsMatch(TxtDpiValue.Text ?? "", @"^\s*\d+")) await ApplyDpiAsync(dpi);
            else AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez entrer une valeur DPI valide (ex: 240).", DateTime.Now));
        }

        private async void BtnSetRes_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            string res = TxtResValue.Text.Trim();
            if (!string.IsNullOrEmpty(res))
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🖥️ Changement Résolution vers {1}...", DateTime.Now, res));
                try
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell wm size {res}");
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Résolution : {1}", DateTime.Now, ex.Message));
                }
            }
        }

        private async void BtnResetDisplay_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔄 Réinitialisation de l'affichage (DPI & Résolution)...", DateTime.Now));

            try
            {
                // 1. Envoie les commandes de remise à zéro au terminal
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell wm size reset");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell wm density reset");

                // 2. Laisse un court délai à Android pour réappliquer les paramètres d'usine
                await System.Threading.Tasks.Task.Delay(500);

                // 3. Récupère et affiche immédiatement la densité et la résolution réinitialisées
                await RefreshToolsStateAsync();
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur réinitialisation affichage : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void BtnSetTimeout_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            string ms = "2147483647"; // Jamais
            if (CmbScreenTimeout.SelectedIndex == 0) ms = "30000"; // 30s
            else if (CmbScreenTimeout.SelectedIndex == 1) ms = "60000"; // 1m
            else if (CmbScreenTimeout.SelectedIndex == 2) ms = "300000"; // 5m
            else if (CmbScreenTimeout.SelectedIndex == 3) ms = "1800000"; // 30m

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⏱️ Application du délai de mise en veille...", DateTime.Now));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system screen_off_timeout {ms}");
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur délai de mise en veille : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- 3. RÉSEAU & DIAGNOSTICS UI ---
        private async void ToggleWifi_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleWifi.IsChecked = !ToggleWifi.IsChecked; return; }
            string cmd = ToggleWifi.IsChecked == true ? "enable" : "disable";
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📶 Wi-Fi : {cmd.ToUpper()}");
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell svc wifi {cmd}");
            }
            catch (Exception ex)
            {
                ToggleWifi.IsChecked = !ToggleWifi.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Wi-Fi : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void ToggleBluetooth_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleBluetooth.IsChecked = !ToggleBluetooth.IsChecked; return; }
            bool wantOn = ToggleBluetooth.IsChecked == true;
            string cmd = wantOn ? "enable" : "disable";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🛜 Bluetooth : {1}", DateTime.Now, cmd.ToUpper()));
            ToggleBluetooth.IsEnabled = false;
            try
            {
                // "svc bluetooth" n'existe pas (ou est sans effet) sur toutes les versions/surcouches : on essaie
                // d'abord "cmd bluetooth_manager" (Android récents), puis "svc bluetooth", et on VÉRIFIE l'état réel
                // ensuite plutôt que de supposer que la commande a fonctionné.
                string attempts = "";
                foreach (string command in new[] { $"cmd bluetooth_manager {cmd}", $"svc bluetooth {cmd}" })
                {
                    var res = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell {command}");
                    string output = ((res.StandardOutput ?? "") + " " + (res.StandardError ?? "")).Trim();
                    attempts += $"\n   • {command} → {(string.IsNullOrEmpty(output) ? $"code {res.ExitCode}" : output)}";

                    // Commande absente ou refusée sur ce terminal : inutile d'attendre un changement d'état.
                    if (!res.IsSuccess || output.IndexOf("No shell command implementation", StringComparison.OrdinalIgnoreCase) >= 0
                        || output.IndexOf("Unknown command", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    // L'activation/désactivation est asynchrone côté Android : on laisse quelques secondes.
                    bool? state = null;
                    for (int i = 0; i < 6 && state != wantOn; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        state = await ReadBluetoothStateAsync(dev.Serial);
                    }
                    if (state == wantOn)
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Bluetooth {1} ({2}).", DateTime.Now, (wantOn ? Loc.T("activé") : Loc.T("désactivé")), command));
                        return;
                    }
                }

                // Dernier recours, valable sur toutes les versions (dont Android 11, où ni "cmd bluetooth_manager"
                // ni "svc bluetooth" ne fonctionnent) : la demande officielle d'Android, qui affiche une fenêtre
                // de confirmation sur le terminal. Le technicien n'a qu'à appuyer sur « Autoriser ».
                string requestAction = wantOn ? "android.bluetooth.adapter.action.REQUEST_ENABLE" : "android.bluetooth.adapter.action.REQUEST_DISABLE";
                var resRequest = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell am start -a {requestAction}");
                string requestOutput = ((resRequest.StandardOutput ?? "") + " " + (resRequest.StandardError ?? "")).Trim();
                bool requestShown = resRequest.IsSuccess && requestOutput.IndexOf("Error", StringComparison.OrdinalIgnoreCase) < 0;
                attempts += $"\n   • demande de confirmation Android → {(requestShown ? "affichée sur le terminal" : requestOutput)}";

                if (requestShown)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 👉 Validez la demande « {1} le Bluetooth » sur l'écran du terminal (20 s)...", DateTime.Now, (wantOn ? Loc.T("Activer") : Loc.T("Désactiver"))));
                    for (int i = 0; i < 40; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        if (await ReadBluetoothStateAsync(dev.Serial) == wantOn)
                        {
                            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Bluetooth {1} (confirmé sur le terminal).", DateTime.Now, (wantOn ? Loc.T("activé") : Loc.T("désactivé"))));
                            return;
                        }
                    }
                }

                // Aucune méthode n'a changé l'état : on remet l'interrupteur sur l'état réel et on explique.
                bool? actual = await ReadBluetoothStateAsync(dev.Serial);
                if (actual.HasValue) ToggleBluetooth.IsChecked = actual.Value;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Le Bluetooth n'a pas changé d'état. Commandes tentées :{1}", DateTime.Now, attempts) +
                                      Loc.T("\n   Sur un terminal géré (MDM / StageNow), le Bluetooth peut être verrouillé par une politique."));
            }
            catch (Exception ex)
            {
                ToggleBluetooth.IsChecked = !ToggleBluetooth.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Bluetooth : {1}", DateTime.Now, ex.Message));
            }
            finally
            {
                ToggleBluetooth.IsEnabled = true;
            }
        }

        /// <summary>État réel du Bluetooth ("settings get global bluetooth_on" : 1 = actif, 0 = inactif, 2 = actif
        /// pour le BLE seul). null si illisible.</summary>
        private async Task<bool?> ReadBluetoothStateAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell settings get global bluetooth_on");
            switch ((res.StandardOutput ?? "").Trim())
            {
                case "1": return true;
                case "0":
                case "2": return false;
                default: return null;
            }
        }

        private async void ToggleAirplane_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleAirplane.IsChecked = !ToggleAirplane.IsChecked; return; }

            string val = ToggleAirplane.IsChecked == true ? "1" : "0";
            string state = ToggleAirplane.IsChecked == true ? "true" : "false";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✈️ Mode Avion : {1}", DateTime.Now, (val == "1" ? "ON" : Loc.T("OFF"))));

            try
            {
                // Depuis Android 7, le shell n'a plus le droit d'envoyer le broadcast AIRPLANE_MODE : le réglage
                // changeait sans que les radios suivent. "cmd connectivity airplane-mode" (Android récents) agit
                // vraiment ; l'ancienne méthode reste en repli pour les terminaux qui ne la connaissent pas.
                var res = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd connectivity airplane-mode {(val == "1" ? "enable" : "disable")}");
                string combined = ((res.StandardOutput ?? "") + (res.StandardError ?? "")).ToLowerInvariant();
                bool modernOk = res.IsSuccess && !combined.Contains("unknown") && !combined.Contains("error") && !combined.Contains("exception");
                if (!modernOk)
                {
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put global airplane_mode_on {val}");
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell am broadcast -a android.intent.action.AIRPLANE_MODE --ez state {state}");
                }
            }
            catch (Exception ex)
            {
                ToggleAirplane.IsChecked = !ToggleAirplane.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Mode Avion : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void ToggleNfc_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleNfc.IsChecked = !ToggleNfc.IsChecked; return; }

            string action = ToggleNfc.IsChecked == true ? "enable" : "disable";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📲 NFC : {1}", DateTime.Now, action.ToUpper()));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell svc nfc {action}");
            }
            catch (Exception ex)
            {
                ToggleNfc.IsChecked = !ToggleNfc.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur NFC : {1}", DateTime.Now, ex.Message));
            }
        }

        /// <summary>
        /// ⚠️ "location_mode" est dépréciée depuis Android 10 mais reste opérationnelle en pratique sur la
        /// plupart des builds OEM observés — à valider sur matériel réel, en particulier Android 12+.
        /// </summary>
        private async void ToggleLocation_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") { ToggleLocation.IsChecked = !ToggleLocation.IsChecked; return; }

            string val = ToggleLocation.IsChecked == true ? "3" : "0";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📍 Localisation réglée sur : {1}", DateTime.Now, (val == "3" ? "ON" : Loc.T("OFF"))));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put secure location_mode {val}");
            }
            catch (Exception ex)
            {
                ToggleLocation.IsChecked = !ToggleLocation.IsChecked;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Localisation : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void BtnDumpUi_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔎 UI Automator : Extraction de l'arborescence XML en cours...", DateTime.Now));
            BtnDumpUi.IsEnabled = false;

            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell uiautomator dump /sdcard/layout_dump.xml");
                string localPath = System.IO.Path.Combine(GetCaptureFolderPath(), $"UI_Dump_{FileNameHelper.Sanitize(dev.Serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} pull /sdcard/layout_dump.xml {AdbManager.QuoteLocalArg(localPath)}");
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell rm /sdcard/layout_dump.xml");

                UpdateCaptureFolderStats();
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ XML généré avec succès !", DateTime.Now));
                if (System.IO.File.Exists(localPath)) OpenWithShell(localPath);
            }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur UI Automator : {1}", DateTime.Now, ex.Message)); }
            finally { BtnDumpUi.IsEnabled = true; }
        }

        // --- 4. VOLUME ---
        private int GetSelectedVolumeStream() =>
            CmbVolumeStream.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int stream) ? stream : 3;

        /// <summary>Applique le volume (index brut) sur le flux sélectionné : appelée par les boutons +/-, Muet, et la validation Entrée.</summary>
        private async Task ApplyVolumeAsync(int index)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            index = Math.Max(0, index);
            int stream = GetSelectedVolumeStream();
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔊 Changement du volume (flux {1}) vers {2}...", DateTime.Now, stream, index));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd media_session volume --stream {stream} --set {index} --show");
                TxtVolumeValue.Text = index.ToString();
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Volume : {1}", DateTime.Now, ex.Message));
            }
        }

        private int ParseVolumeOrDefault() => int.TryParse(TxtVolumeValue.Text.Trim(), out int v) ? v : 0;

        private async void BtnVolumeMinus_Click(object sender, RoutedEventArgs e) => await ApplyVolumeAsync(ParseVolumeOrDefault() - 1);
        private async void BtnVolumePlus_Click(object sender, RoutedEventArgs e) => await ApplyVolumeAsync(ParseVolumeOrDefault() + 1);
        private async void BtnVolumeMute_Click(object sender, RoutedEventArgs e) => await ApplyVolumeAsync(0);

        private async void TxtVolumeValue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            if (int.TryParse(TxtVolumeValue.Text.Trim(), out int v)) await ApplyVolumeAsync(v);
            else AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez entrer un index de volume valide (ex: 8).", DateTime.Now));
        }

        /// <summary>
        /// ⚠️ Le plus expérimental des ajouts de ce module : "service call audio" utilise une transaction
        /// numérotée par position dans l'interface AIDL du service audio, qui n'est pas une API publique
        /// stable et peut varier d'une version d'Android à l'autre — non testé sur matériel réel.
        /// </summary>
        private async Task SetRingerModeAsync(int mode)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device") return;

            string label = mode == 2 ? "Son" : mode == 1 ? "Vibreur" : "Silencieux";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔔 Mode sonnerie réglé sur {1}...", DateTime.Now, label));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell service call audio 8 i32 {mode}");
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Mode sonnerie : {1}", DateTime.Now, ex.Message));
            }
        }

        private async void BtnRingerNormal_Click(object sender, RoutedEventArgs e) => await SetRingerModeAsync(2);
        private async void BtnRingerVibrate_Click(object sender, RoutedEventArgs e) => await SetRingerModeAsync(1);
        private async void BtnRingerSilent_Click(object sender, RoutedEventArgs e) => await SetRingerModeAsync(0);

        // 🔒 Drapeau consulté à la fermeture de la fenêtre pour empêcher une interruption en plein flashage OS.
        private bool _isSideloading = false;

        private async void BtnSideloadOs_Click(object sender, RoutedEventArgs e)
        {
            bool confirm = await ShowCustomDialogAsync(Loc.T("⚠️ ATTENTION SIDELOAD"), Loc.T("Pour flasher un OS, assurez-vous qu'UN SEUL terminal est branché en USB à ce PC et qu'il est sur l'écran 'Apply update from ADB'.\n\nDébranchez tous les autres appareils avant de continuer !"), Loc.T("J'ai compris"), Loc.T("Annuler"), true);
            if (!confirm) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Fichiers de mise à jour OS (*.zip)|*.zip",
                Title = Loc.T("Sélectionnez le fichier OS_Update.zip")
            };

            if (dlg.ShowDialog() == true)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📦 Démarrage du Sideload avec : {1}", DateTime.Now, System.IO.Path.GetFileName(dlg.FileName)));
                AppendConsole(Loc.F("\n⚠️ NE DÉBRANCHEZ SURTOUT PAS LE TERMINAL ! PATIENTEZ..."));

                _isSideloading = true;
                try
                {
                    // AdbTransferProcess lit stdout ET stderr (auparavant ni l'un ni l'autre n'était lu) et
                    // rattache le process au Job Object. Pas d'annulation possible : on n'interrompt jamais
                    // un flashage en cours.
                    var transfer = new AdbTransferProcess();
                    var (exitOk, output) = await transfer.RunAsync(
                        _adbExePath,
                        $"sideload {AdbManager.QuoteLocalArg(dlg.FileName)}",
                        null,
                        System.Threading.CancellationToken.None);

                    // Certains recovery ferment la connexion en fin de transfert, ce qui donne un code de sortie
                    // non nul alors que le transfert est complet : "Total xfer" confirme alors la réussite.
                    bool transferComplete = exitOk || (output ?? "").IndexOf("Total xfer", StringComparison.OrdinalIgnoreCase) >= 0;
                    string lastLines = string.Join(" | ", (output ?? "")
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0)
                        .Reverse().Take(3).Reverse());

                    if (transferComplete)
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Sideload terminé ! Suivez l'installation sur l'écran du terminal. ({1})", DateTime.Now, lastLines));
                    }
                    else
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Le Sideload a échoué : {1}", DateTime.Now, lastLines) +
                                              Loc.T("\n   Vérifiez que le terminal est bien sur 'Apply update from ADB' et qu'un seul appareil est branché."));
                    }
                }
                catch (Exception ex)
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Sideload : {1}", DateTime.Now, ex.Message));
                }
                finally
                {
                    _isSideloading = false;
                }
            }
        }
    }
}
