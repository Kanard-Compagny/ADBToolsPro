using ADBToolsNew.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ADBToolsNew
{
    public partial class MainWindow : Window
    {
        // ==========================================
        // === ONGLET 5 : OUTILS AVANCÉS (N2/N3) ====
        // ==========================================
        // Tout l'état est relu en UN appel adb (TerminalToolsState) à l'ouverture de l'onglet, au changement de
        // terminal et avec « Relire l'état ». Chaque bascule vérifie ensuite l'état réel du terminal : si la
        // commande n'a pas pris, l'interrupteur revient sur l'état réel et l'écran Android concerné s'ouvre sur
        // le terminal pour le faire à la main.

        private TerminalToolsState _toolsState;
        private bool _toolsRefreshing;

        private AndroidDevice ToolsDevice()
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            return dev != null && dev.State == "device" ? dev : null;
        }

        private async Task RefreshToolsStateAsync()
        {
            var dev = ToolsDevice();
            UpdateStayOnBadge();
            if (dev == null)
            {
                _toolsState = null;
                TxtToolsDevice.Text = Loc.T("Aucun terminal connecté");
                return;
            }
            if (_toolsRefreshing) return;
            _toolsRefreshing = true;
            BtnRefreshTools.IsEnabled = false;
            try
            {
                int stream = GetSelectedVolumeStream();
                DateTime pcUtc = DateTime.UtcNow;
                var sections = await _adbManager.RunShellSectionsAsync(dev.Serial, TimeSpan.FromSeconds(20), TerminalToolsState.Commands(stream));
                var t = TerminalToolsState.Parse(sections, stream);
                if ((DeviceComboBox.SelectedItem as AndroidDevice)?.Serial != dev.Serial) return; // terminal changé entre-temps
                _toolsState = t;
                ApplyToolsState(t, pcUtc);
            }
            catch (Exception ex)
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Lecture de l'état du terminal impossible : {1}", DateTime.Now, ex.Message));
            }
            finally
            {
                _toolsRefreshing = false;
                BtnRefreshTools.IsEnabled = true;
            }
        }

        private void ApplyToolsState(TerminalToolsState t, DateTime pcUtc)
        {
            var head = new List<string>();
            string name = string.Join(" ", new[] { VendorModules.BrandOf(t.Manufacturer), t.Model }.Where(x => !string.IsNullOrEmpty(x)));
            if (name.Length > 0) head.Add(name);
            if (t.AndroidVersion != null) head.Add(Loc.F("Android {0}", t.AndroidVersion));
            head.Add(Loc.F("état relu à {0:HH:mm}", DateTime.Now));
            TxtToolsDevice.Text = string.Join(" · ", head);

            // Touches : la gâchette de scan seulement là où elle a été vérifiée.
            ScanTriggerRow.Visibility = t.ScanTriggerCommand() != null ? Visibility.Visible : Visibility.Collapsed;

            // États
            SetSwitch(ToggleWifi, "Wi-Fi", t.Wifi, null);
            SetSwitch(ToggleBluetooth, "Bluetooth", t.Bluetooth, t.HasBluetooth ? null : Loc.T("absent sur ce terminal"));
            SetSwitch(ToggleAirplane, "Mode avion", t.Airplane, null);
            SetSwitch(ToggleNfc, "NFC", t.Nfc, t.HasNfc ? null : Loc.T("absent sur ce terminal"));
            SetSwitch(ToggleLocation, "Localisation", t.Location, null);
            SetSwitch(ToggleMobileData, "Données mobiles", t.MobileData,
                !t.HasTelephony ? Loc.T("pas de réseau mobile") : t.SimAbsent ? Loc.T("pas de carte SIM") : null);
            SetSwitch(ToggleRotation, "Rotation automatique", t.Rotation, null);
            SetSwitch(ToggleDarkMode, "Mode sombre", t.DarkMode, t.Sdk.HasValue && t.Sdk < 29 ? Loc.T("Android 10 minimum") : null);

            // Affichage
            if (t.BrightnessPercent.HasValue) TxtBrightness.Text = t.BrightnessPercent.Value.ToString();
            if (t.Dpi.HasValue) TxtDpiValue.Text = t.Dpi.Value.ToString();
            TxtDpiNote.Text = t.DpiOverridden && t.PhysicalDpi.HasValue ? Loc.F("personnalisée (origine {0})", t.PhysicalDpi.Value) : "";
            if (t.Resolution != null) TxtResValue.Text = t.Resolution;
            ShowTimeout(t.TimeoutMs);

            // Son
            ShowVolume(t.VolumeIndex, t.VolumeMax);
            ShowRinger(t.RingerMode);

            // Date, heure, langue
            long? offset = t.ClockOffsetSeconds(pcUtc);
            if (t.DeviceEpoch.HasValue)
            {
                var local = DateTimeOffset.FromUnixTimeSeconds(t.DeviceEpoch.Value).ToLocalTime();
                TxtDeviceClock.Text = offset.HasValue && Math.Abs(offset.Value) >= TerminalToolsState.ClockWarnSeconds
                    ? Loc.F("{0:dd/MM HH:mm:ss} (PC : {1:dd/MM HH:mm:ss})", local, pcUtc.ToLocalTime())
                    : Loc.F("{0:dd/MM HH:mm:ss}, à l'heure du PC", local);
            }
            else TxtDeviceClock.Text = "—";
            if (offset.HasValue && Math.Abs(offset.Value) >= TerminalToolsState.ClockWarnSeconds)
            {
                TxtClockWarning.Text = Loc.F("Le terminal a {0} {1} sur le PC. Une heure fausse peut faire échouer les certificats et le Wi-Fi entreprise, et fausse l'heure des journaux.",
                    TerminalToolsState.FormatDuration(offset.Value), offset.Value > 0 ? Loc.T("d'avance") : Loc.T("de retard"));
                ClockWarning.Visibility = Visibility.Visible;
            }
            else ClockWarning.Visibility = Visibility.Collapsed;
            SetSwitch(ToggleAutoTime, "Heure automatique (réseau)", t.AutoTime, null);
            SetSwitch(ToggleAutoTimeZone, "Fuseau automatique", t.AutoTimeZone, null);
            TxtTimeZoneLocale.Text = string.Join(" · ", new[] { t.TimeZone, TerminalToolsState.LocaleName(t.Locale) }.Where(x => !string.IsNullOrEmpty(x)));
            if (TxtTimeZoneLocale.Text.Length == 0) TxtTimeZoneLocale.Text = "—";

            // Écran allumé quand branché
            ToggleStayOn.IsChecked = t.StayOnPlugged.HasValue ? t.StayOnPlugged.Value != 0 : (bool?)null;
            UpdateStayOnBadge();
        }

        /// <summary>Interrupteur d'état : valeur lue (null = inconnue), ou grisé avec la raison quand la fonction
        /// n'existe pas sur ce terminal.</summary>
        private static void SetSwitch(ToggleButton tb, string label, bool? value, string unavailableReason)
        {
            bool available = unavailableReason == null;
            tb.Content = available ? Loc.T(label) : Loc.F("{0} · {1}", Loc.T(label), unavailableReason);
            tb.IsEnabled = available;
            tb.IsChecked = available ? value : false;
        }

        private async void BtnRefreshTools_Click(object sender, RoutedEventArgs e) => await RefreshToolsStateAsync();

        /// <summary>Lit une valeur sur le terminal (une commande shell, sortie nettoyée).</summary>
        private async Task<string> ReadDeviceValueAsync(string serial, string command)
        {
            var s = await _adbManager.RunShellSectionsAsync(serial, TimeSpan.FromSeconds(10), command);
            return (s[0] ?? "").Trim();
        }

        private static bool CommandRefused(AdbCommandResult r)
        {
            string o = ((r.StandardOutput ?? "") + " " + (r.StandardError ?? ""));
            return System.Text.RegularExpressions.Regex.IsMatch(o,
                @"Unknown command|No shell command implementation|not found|inaccessible|Exception|Error|Usage:|Security", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// Bascule un état du terminal : essaie les commandes dans l'ordre, relit l'état réel après chacune et
        /// s'arrête dès qu'il correspond. Si rien n'a pris, l'interrupteur revient à l'état réel et, quand il
        /// existe, l'écran Android du réglage s'ouvre sur le terminal.
        /// </summary>
        private async Task SwitchOnDeviceAsync(ToggleButton tb, string label, string[] enable, string[] disable,
            string readCommand, Func<string, bool?> parse, string settingsAction)
        {
            var dev = ToolsDevice();
            if (dev == null) { tb.IsChecked = !(tb.IsChecked == true); return; }
            bool want = tb.IsChecked == true;
            tb.IsEnabled = false;
            try
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] {1} : {2}", DateTime.Now, Loc.T(label), want ? Loc.T("activation") : Loc.T("désactivation")));
                var tried = new List<string>();
                foreach (string cmd in want ? enable : disable)
                {
                    var r = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell {AdbManager.QuoteLocalArg(cmd)}");
                    tried.Add(cmd);
                    if (CommandRefused(r)) continue;
                    for (int i = 0; i < 6; i++)
                    {
                        await Task.Delay(i == 0 ? 250 : 400);
                        if (parse(await ReadDeviceValueAsync(dev.Serial, readCommand)) == want)
                        {
                            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ {1} {2}.", DateTime.Now, Loc.T(label), want ? Loc.T("activé") : Loc.T("désactivé")));
                            return;
                        }
                    }
                }
                bool? actual = parse(await ReadDeviceValueAsync(dev.Serial, readCommand));
                tb.IsChecked = actual ?? !want;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ {1} n'a pas changé (essayé : {2}).", DateTime.Now, Loc.T(label), string.Join(" ; ", tried)));
                if (settingsAction != null && await OpenOnDeviceAsync(dev.Serial, settingsAction))
                    AppendConsole(Loc.T("\n   L'écran du réglage est ouvert sur le terminal pour le faire à la main. Sur un terminal géré (MDM, StageNow), le réglage peut être verrouillé."));
            }
            catch (Exception ex)
            {
                tb.IsChecked = !want;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ {1} : {2}", DateTime.Now, Loc.T(label), ex.Message));
            }
            finally { tb.IsEnabled = true; }
        }

        // --- 1. TOUCHES ET RACCOURCIS ---

        private async void BtnKey_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (dev == null || !(sender is Button btn) || btn.Tag == null) return;
            string keycode = btn.Tag.ToString();
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🕹️ Touche « {1} » envoyée (code {2})", DateTime.Now, btn.Content, keycode));
            try { await _adbManager.ExecuteAsync($"-s {dev.Serial} shell input keyevent {keycode}"); }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur envoi touche : {1}", DateTime.Now, ex.Message)); }
        }

        private async void BtnScanTrigger_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            string cmd = _toolsState?.ScanTriggerCommand();
            if (dev == null || cmd == null) return;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔫 Lecture déclenchée (DataWedge). Le résultat arrive dans l'appli au premier plan du terminal.", DateTime.Now));
            try { await _adbManager.ExecuteAsync($"-s {dev.Serial} shell {cmd}"); }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Déclenchement de la lecture : {1}", DateTime.Now, ex.Message)); }
        }

        /// <summary>Ouvre un écran Android standard sur le terminal (réveillé d'abord). false si l'écran n'existe pas.</summary>
        private async Task<bool> OpenOnDeviceAsync(string serial, string action)
        {
            await _adbManager.ExecuteAsync($"-s {serial} shell input keyevent 224");
            var r = await _adbManager.ExecuteAsync($"-s {serial} shell am start -a {action}");
            return r.IsSuccess && !TerminalToolsState.StartFailed((r.StandardOutput ?? "") + (r.StandardError ?? ""));
        }

        private async void BtnOpenOnDevice_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (dev == null || !(sender is Button btn) || !(btn.Tag is string action)) return;
            string label = btn.Content?.ToString();
            try
            {
                if (await OpenOnDeviceAsync(dev.Serial, action))
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📱 « {1} » ouvert sur le terminal.", DateTime.Now, label));
                else
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ « {1} » n'existe pas sur ce terminal (écran retiré par le constructeur ou par une politique).", DateTime.Now, label));
            }
            catch (Exception ex) { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ {1} : {2}", DateTime.Now, label, ex.Message)); }
        }

        // --- 2. ÉTATS ---

        private async void ToggleWifi_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleWifi, "Wi-Fi",
                new[] { "svc wifi enable", "cmd wifi set-wifi-enabled enabled" },
                new[] { "svc wifi disable", "cmd wifi set-wifi-enabled disabled" },
                "settings get global wifi_on", TerminalToolsState.OnOff, "android.settings.WIFI_SETTINGS");

        private async void ToggleNfc_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleNfc, "NFC",
                new[] { "svc nfc enable" }, new[] { "svc nfc disable" },
                "dumpsys nfc 2>/dev/null | grep -m1 mState", TerminalToolsState.ParseNfcState, "android.settings.NFC_SETTINGS");

        // "cmd location set-location-enabled" (Android 11+) agit vraiment ; "location_mode" (déprécié depuis
        // Android 10) reste le repli des terminaux plus anciens. L'état est relu dans location_mode, tenu à jour
        // par Android sur toutes les versions.
        private async void ToggleLocation_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleLocation, "Localisation",
                new[] { "cmd location set-location-enabled true", "settings put secure location_mode 3" },
                new[] { "cmd location set-location-enabled false", "settings put secure location_mode 0" },
                "settings get secure location_mode", TerminalToolsState.OnOff, "android.settings.LOCATION_SOURCE_SETTINGS");

        private async void ToggleMobileData_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleMobileData, "Données mobiles",
                new[] { "svc data enable" }, new[] { "svc data disable" },
                "settings get global mobile_data", TerminalToolsState.OnOff, "android.settings.DATA_ROAMING_SETTINGS");

        private async void ToggleRotation_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleRotation, "Rotation automatique",
                new[] { "settings put system accelerometer_rotation 1" }, new[] { "settings put system accelerometer_rotation 0" },
                "settings get system accelerometer_rotation", TerminalToolsState.OnOff, "android.settings.DISPLAY_SETTINGS");

        private async void ToggleDarkMode_Click(object sender, RoutedEventArgs e) =>
            await SwitchOnDeviceAsync(ToggleDarkMode, "Mode sombre",
                new[] { "cmd uimode night yes" }, new[] { "cmd uimode night no" },
                "cmd uimode night", o => o.ToLowerInvariant().Contains("yes") ? true : o.ToLowerInvariant().Contains("no") ? false : (bool?)null,
                "android.settings.DISPLAY_SETTINGS");

        private async void ToggleAutoTime_Click(object sender, RoutedEventArgs e)
        {
            await SwitchOnDeviceAsync(ToggleAutoTime, "Heure automatique (réseau)",
                new[] { "settings put global auto_time 1" }, new[] { "settings put global auto_time 0" },
                "settings get global auto_time", TerminalToolsState.OnOff, "android.settings.DATE_SETTINGS");
            // L'heure se recale dès que le réseau répond : on relit pour mettre l'écart à jour.
            if (ToggleAutoTime.IsChecked == true) { await Task.Delay(2500); await RefreshToolsStateAsync(); }
        }

        private async void ToggleAutoTimeZone_Click(object sender, RoutedEventArgs e)
        {
            await SwitchOnDeviceAsync(ToggleAutoTimeZone, "Fuseau automatique",
                new[] { "settings put global auto_time_zone 1" }, new[] { "settings put global auto_time_zone 0" },
                "settings get global auto_time_zone", TerminalToolsState.OnOff, "android.settings.DATE_SETTINGS");
            if (ToggleAutoTimeZone.IsChecked == true) { await Task.Delay(2500); await RefreshToolsStateAsync(); }
        }


        // Bluetooth et mode avion : méthodes éprouvées (plusieurs commandes selon la version, état vérifié).
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

        // --- 3. AFFICHAGE ---

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

        // --- Mise en veille : le bouton du réglage lu sur le terminal est coché ; un clic applique. ---

        private RadioButton[] TimeoutButtons => new[] { RbTimeout0, RbTimeout1, RbTimeout2, RbTimeout3, RbTimeout4 };

        private void ShowTimeout(int? ms)
        {
            int idx = TerminalToolsState.TimeoutIndex(ms);
            var buttons = TimeoutButtons;
            for (int i = 0; i < buttons.Length; i++) buttons[i].IsChecked = i == idx;
            if (idx < 0 && ms.HasValue)
            {
                TxtTimeoutCustom.Text = Loc.F("Réglage actuel : {0}", TerminalToolsState.FormatTimeout(ms.Value));
                TxtTimeoutCustom.Visibility = Visibility.Visible;
            }
            else TxtTimeoutCustom.Visibility = Visibility.Collapsed;
        }

        private async void TimeoutChoice_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (!(sender is RadioButton rb) || !int.TryParse(rb.Tag?.ToString(), out int idx)) return;
            if (dev == null) { ShowTimeout(_toolsState?.TimeoutMs); return; }
            int ms = TerminalToolsState.TimeoutChoices[idx];
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⏱️ Mise en veille : {1}", DateTime.Now, TerminalToolsState.FormatTimeout(ms)));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put system screen_off_timeout {ms}");
                int.TryParse(await ReadDeviceValueAsync(dev.Serial, "settings get system screen_off_timeout"), out int read);
                if (_toolsState != null) _toolsState.TimeoutMs = read;
                ShowTimeout(read);
                if (TerminalToolsState.TimeoutIndex(read) != idx)
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ La mise en veille n'a pas changé : le réglage est peut-être verrouillé (MDM, StageNow).", DateTime.Now));
            }
            catch (Exception ex)
            {
                ShowTimeout(_toolsState?.TimeoutMs);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur délai de mise en veille : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- 4. SON ---

        private int GetSelectedVolumeStream() =>
            CmbVolumeStream?.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int stream) ? stream : 3;

        private int? _volumeMax;

        private void ShowVolume(int? index, int? max)
        {
            _volumeMax = max;
            TxtVolumeValue.Text = index?.ToString() ?? "";
            TxtVolumeMax.Text = max.HasValue ? Loc.F("sur {0}", max.Value) : "";
        }

        private async Task ReadVolumeAsync(string serial)
        {
            string o = await ReadDeviceValueAsync(serial, $"cmd media_session volume --stream {GetSelectedVolumeStream()} --get 2>&1");
            if (TerminalToolsState.TryParseVolume(o, out int v, out int max)) ShowVolume(v, max);
            else ShowVolume(null, null);
        }

        private async void CmbVolumeStream_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtVolumeValue == null || DeviceComboBox == null) return;
            var dev = ToolsDevice();
            if (dev == null) return;
            try { await ReadVolumeAsync(dev.Serial); } catch { }
        }

        /// <summary>Applique le volume sur le flux choisi (boutons −/+, Couper, Entrée), puis relit la valeur réelle.</summary>
        private async Task ApplyVolumeAsync(int index)
        {
            var dev = ToolsDevice();
            if (dev == null) return;
            index = Math.Max(0, index);
            if (_volumeMax.HasValue) index = Math.Min(_volumeMax.Value, index);
            int stream = GetSelectedVolumeStream();
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔊 Volume ({1}) réglé à {2}", DateTime.Now, (CmbVolumeStream.SelectedItem as ComboBoxItem)?.Content, index));
            try
            {
                await _adbManager.ExecuteAsync($"-s {dev.Serial} shell cmd media_session volume --stream {stream} --set {index} --show");
                await ReadVolumeAsync(dev.Serial);
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

        private void ShowRinger(int? mode)
        {
            RbRingerNormal.IsChecked = mode == 2;
            RbRingerVibrate.IsChecked = mode == 1;
            RbRingerSilent.IsChecked = mode == 0;
        }

        /// <summary>
        /// Mode sonnerie. Android n'a pas de commande shell stable pour ça : on essaie "cmd audio" puis l'ancien
        /// appel au service audio, et on RELIT mode_ringer. Si rien n'a pris, l'écran Son s'ouvre sur le terminal.
        /// </summary>
        private async void RingerChoice_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (!(sender is RadioButton rb) || !int.TryParse(rb.Tag?.ToString(), out int mode)) return;
            if (dev == null) { ShowRinger(_toolsState?.RingerMode); return; }
            string name = mode == 2 ? "NORMAL" : mode == 1 ? "VIBRATE" : "SILENT";
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔔 Mode sonnerie : {1}", DateTime.Now, rb.Content));
            try
            {
                foreach (string cmd in new[] { $"cmd audio set-ringer-mode {name}", $"service call audio 8 i32 {mode}" })
                {
                    var r = await _adbManager.ExecuteAsync($"-s {dev.Serial} shell {cmd}");
                    if (CommandRefused(r)) continue;
                    await Task.Delay(400);
                    if (int.TryParse(await ReadDeviceValueAsync(dev.Serial, "settings get global mode_ringer"), out int now) && now == mode)
                    {
                        if (_toolsState != null) _toolsState.RingerMode = now;
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Mode sonnerie appliqué ({1}).", DateTime.Now, cmd));
                        return;
                    }
                }
                int? actual = int.TryParse(await ReadDeviceValueAsync(dev.Serial, "settings get global mode_ringer"), out int a) ? a : (int?)null;
                ShowRinger(actual);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Le mode sonnerie n'a pas changé sur ce terminal.", DateTime.Now));
                if (await OpenOnDeviceAsync(dev.Serial, "android.settings.SOUND_SETTINGS"))
                    AppendConsole(Loc.T("\n   L'écran Son est ouvert sur le terminal pour le faire à la main."));
            }
            catch (Exception ex)
            {
                ShowRinger(_toolsState?.RingerMode);
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Erreur Mode sonnerie : {1}", DateTime.Now, ex.Message));
            }
        }

        // --- 5. ÉCRAN ALLUMÉ QUAND BRANCHÉ (avec garde-fou) ---
        // Le réglage d'origine est noté dans les réglages du poste (« numéro de série|valeur ») avant d'être changé.
        // Il est remis au changement de terminal, à la fermeture d'ADB Tools, ou au retour d'un terminal débranché
        // pendant qu'il était actif. Tant qu'il est noté pour le terminal choisi, un badge orange le rappelle.

        private const int StayOnAll = 7; // secteur + USB + sans fil, comme « svc power stayon true »
        private string _stayOnActiveSerial;

        private static Dictionary<string, int> PendingStayOn()
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in AppSettings.Current.StayOnRestore ?? new List<string>())
            {
                int bar = e.LastIndexOf('|');
                if (bar > 0 && int.TryParse(e.Substring(bar + 1), out int v)) d[e.Substring(0, bar)] = v;
            }
            return d;
        }

        private static void SavePendingStayOn(Dictionary<string, int> d)
        {
            AppSettings.Current.StayOnRestore = d.Select(kv => kv.Key + "|" + kv.Value).ToList();
            AppSettings.Save();
        }

        private void UpdateStayOnBadge()
        {
            if (BtnStayOnBadge == null) return;
            string serial = ToolsDevice()?.Serial;
            BtnStayOnBadge.Visibility = serial != null && PendingStayOn().ContainsKey(serial) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ToggleStayOn_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (dev == null) { ToggleStayOn.IsChecked = !(ToggleStayOn.IsChecked == true); return; }
            bool want = ToggleStayOn.IsChecked == true;
            ToggleStayOn.IsEnabled = false;
            try
            {
                if (want)
                {
                    var pending = PendingStayOn();
                    if (!pending.ContainsKey(dev.Serial))
                    {
                        int.TryParse(await ReadDeviceValueAsync(dev.Serial, "settings get global stay_on_while_plugged_in"), out int original);
                        pending[dev.Serial] = original;
                        SavePendingStayOn(pending);
                    }
                    await _adbManager.ExecuteAsync($"-s {dev.Serial} shell settings put global stay_on_while_plugged_in {StayOnAll}");
                    _stayOnActiveSerial = dev.Serial;
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💡 Écran maintenu allumé tant que le terminal est branché. Remis comme avant au changement de terminal ou à la fermeture.", DateTime.Now));
                }
                else await RestoreStayOnAsync(dev.Serial, true);
                int.TryParse(await ReadDeviceValueAsync(dev.Serial, "settings get global stay_on_while_plugged_in"), out int now);
                ToggleStayOn.IsChecked = now != 0;
            }
            catch (Exception ex)
            {
                ToggleStayOn.IsChecked = !want;
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Écran allumé quand branché : {1}", DateTime.Now, ex.Message));
            }
            finally
            {
                ToggleStayOn.IsEnabled = true;
                UpdateStayOnBadge();
            }
        }

        private async void BtnStayOnBadge_Click(object sender, RoutedEventArgs e)
        {
            var dev = ToolsDevice();
            if (dev == null) return;
            await RestoreStayOnAsync(dev.Serial, true);
            if (_toolsState != null) ToggleStayOn.IsChecked = false;
            UpdateStayOnBadge();
        }

        /// <summary>Remet le réglage d'origine noté pour ce terminal. Sans valeur notée et si <paramref name="offIfUnknown"/>,
        /// l'éteint simplement (l'utilisateur a coupé lui-même un réglage qu'il n'avait pas allumé ici).</summary>
        private async Task RestoreStayOnAsync(string serial, bool offIfUnknown)
        {
            var pending = PendingStayOn();
            bool known = pending.TryGetValue(serial, out int original);
            if (!known && !offIfUnknown) return;
            if (!await PutStayOnAsync(serial, known ? original : 0)) return; // terminal injoignable : la valeur reste notée, remise à son retour
            if (known) { pending.Remove(serial); SavePendingStayOn(pending); }
            if (_stayOnActiveSerial == serial) _stayOnActiveSerial = null;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💡 Mise en veille de l'écran branché remise comme avant sur {1}.", DateTime.Now, serial));
        }

        /// <summary>Appelé à chaque changement de terminal choisi : remet le terminal quitté, et celui qui revient
        /// avec une valeur encore notée (débranché pendant que le réglage était actif, ou ADB Tools fermé sans lui).</summary>
        private async Task StayOnGuardOnDeviceChangeAsync(string newSerial)
        {
            try
            {
                if (_stayOnActiveSerial != null && _stayOnActiveSerial != newSerial)
                {
                    string left = _stayOnActiveSerial;
                    _stayOnActiveSerial = null;
                    await RestoreStayOnAsync(left, false);
                }
                if (newSerial != null && _stayOnActiveSerial != newSerial && PendingStayOn().ContainsKey(newSerial))
                    await RestoreStayOnAsync(newSerial, false);
            }
            catch { }
            UpdateStayOnBadge();
        }

        private async Task<bool> PutStayOnAsync(string serial, int value)
        {
            var r = await _adbManager.ExecuteAsync($"-s {serial} shell settings put global stay_on_while_plugged_in {value}", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return r.IsSuccess;
        }

        /// <summary>Fermeture d'ADB Tools : remet chaque terminal joignable (quelques secondes au plus, sans toucher
        /// à l'interface, qui attend). Les injoignables restent notés et seront remis à leur retour.</summary>
        private void RestoreStayOnOnExit()
        {
            if (PendingStayOn().Count == 0) return;
            try
            {
                Task.Run(async () =>
                {
                    var pending = PendingStayOn();
                    foreach (var serial in pending.Keys.ToList())
                        if (await PutStayOnAsync(serial, pending[serial]).ConfigureAwait(false)) pending.Remove(serial);
                    SavePendingStayOn(pending);
                }).Wait(TimeSpan.FromSeconds(6));
            }
            catch { }
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

        // 🔒 Drapeau consulté à la fermeture de la fenêtre (et avant un redémarrage d'ADB Tools) pour empêcher
        // une interruption en plein flashage OS. La mise à jour OS passe par MainWindow.MassOsUpdate.cs (un ou
        // plusieurs terminaux) ; _massOsRunning y compte les terminaux en cours.
        private int _massOsRunning;
        private bool _isSideloading => _massOsRunning > 0;
    }
}
