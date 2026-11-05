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
        // 1. TERMINAL ADB INTELLIGENT MULTI-LIGNES
        // ==========================================
        private string FormatAdbCommand(string input)
        {
            input = input.Trim();
            if (input.StartsWith("adb ", StringComparison.OrdinalIgnoreCase)) input = input.Substring(4).Trim();
            if (input.StartsWith("adb.exe ", StringComparison.OrdinalIgnoreCase)) input = input.Substring(8).Trim();

            // Sous-commandes adb "natives" (à ne pas préfixer par "shell"). Liste complétée : install-multiple,
            // forward, reverse, sideload, get-state, pair... étaient auparavant envoyées à tort en "adb shell ...".
            string[] nativeCommands =
            {
                "shell", "push", "pull", "sync", "install", "install-multiple", "install-multi-package", "uninstall",
                "logcat", "reboot", "reboot-bootloader", "devices", "tcpip", "usb", "connect", "disconnect", "pair",
                "forward", "reverse", "kill-server", "start-server", "reconnect", "root", "unroot", "remount",
                "disable-verity", "enable-verity", "bugreport", "sideload", "get-state", "get-serialno", "get-devpath",
                "wait-for-device", "wait-for-recovery", "wait-for-sideload", "wait-for-bootloader", "wait-for-disconnect",
                "jdwp", "emu", "backup", "restore", "version", "help", "mdns", "features", "host-features"
            };
            string firstWord = input.Split(' ')[0].ToLower();

            if (!nativeCommands.Contains(firstWord) && !firstWord.StartsWith("-")) // "-d", "-e", "-t 2"... = options adb globales
            {
                input = "shell " + input;
            }
            return input;
        }

        private void AddCommandToHistory(string singleCmd)
        {
            singleCmd = singleCmd.Trim();
            if (string.IsNullOrEmpty(singleCmd)) return;

            if (!CmbCommandHistory.Items.Contains(singleCmd))
            {
                CmbCommandHistory.Items.Insert(0, singleCmd);
                if (CmbCommandHistory.Items.Count > 20)
                {
                    CmbCommandHistory.Items.RemoveAt(20);
                }
            }
        }

        private async void BtnExecuteCommand_Click(object sender, RoutedEventArgs e)
        {
            string rawInput = TxtAdbCommand.Text.Trim();
            if (string.IsNullOrEmpty(rawInput)) return;

            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || string.IsNullOrEmpty(selectedDevice.State) || selectedDevice.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Aucun appareil valide sélectionné.", DateTime.Now));
                return;
            }

            BtnExecuteCommand.IsEnabled = false;
            TxtAdbCommand.IsEnabled = false;

            var lines = rawInput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(l => l.Trim())
                                .Where(l => !string.IsNullOrEmpty(l))
                                .ToArray();

            int totalCommands = lines.Length;

            for (int i = 0; i < totalCommands; i++)
            {
                string singleCmd = lines[i];
                string formattedCmd = FormatAdbCommand(singleCmd);
                int cmdIndex = i + 1;

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ┌─── Exec [{1}/{2}] : adb -s {3} {4}", DateTime.Now, cmdIndex, totalCommands, selectedDevice.Serial, formattedCmd));

                try
                {
                    var result = await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} {formattedCmd}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        string output = result.StandardOutput?.Trim() ?? "";
                        bool isMultiLine = output.Contains("\n");

                        if (result.IsSuccess)
                        {
                            if (!string.IsNullOrWhiteSpace(output))
                            {
                                if (isMultiLine)
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Retour ADB [{1}/{2}] :\n{3}", DateTime.Now, cmdIndex, totalCommands, output));
                                else
                                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Retour ADB [{1}/{2}] : {3}", DateTime.Now, cmdIndex, totalCommands, output));
                            }
                            else
                            {
                                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Retour ADB [{1}/{2}] : (Succès - Aucune donnée retournée)", DateTime.Now, cmdIndex, totalCommands));
                            }

                            AddCommandToHistory(singleCmd);
                        }
                        else
                        {
                            string errOutput = result.StandardError?.Trim() ?? "";
                            bool errMultiLine = errOutput.Contains("\n");

                            if (errMultiLine)
                                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Erreur ADB [{1}/{2}] :\n{3}", DateTime.Now, cmdIndex, totalCommands, errOutput));
                            else
                                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Erreur ADB [{1}/{2}] : {3}", DateTime.Now, cmdIndex, totalCommands, errOutput));
                        }
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        AppendConsole(Loc.F("\n[{0:HH:mm:ss}] └── Exception [{1}/{2}] : {3}", DateTime.Now, cmdIndex, totalCommands, ex.Message));
                    });
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                BtnExecuteCommand.IsEnabled = true;
                TxtAdbCommand.IsEnabled = true;
                TxtAdbCommand.Focus();
                if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
            });
        }

        private void TxtAdbCommand_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
            {
                BtnExecuteCommand_Click(sender, e);
            }
        }

        private void BtnClearCommand_Click(object sender, RoutedEventArgs e)
        {
            TxtAdbCommand.Text = string.Empty;
            TxtAdbCommand.Focus();
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            CmbCommandHistory.Items.Clear();
        }

        private void CmbCommandHistory_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CmbCommandHistory.SelectedItem != null)
            {
                TxtAdbCommand.Text = CmbCommandHistory.SelectedItem.ToString();
            }
        }

        // ==========================================
        // 2. DESSIN GÉOMÉTRIQUE DES JAUGES
        // ==========================================
        private void UpdateGaugePath(System.Windows.Shapes.Path pathControl, double percentage)
        {
            percentage = Math.Max(0, Math.Min(100, percentage)) / 100.0;
            if (percentage <= 0.001) { pathControl.Data = null; return; }

            double startAngle = 135, sweepAngle = 270 * percentage, endAngle = startAngle + sweepAngle, radius = 40, cx = 50, cy = 50;
            double startRad = startAngle * Math.PI / 180.0, endRad = endAngle * Math.PI / 180.0;
            double startX = cx + radius * Math.Cos(startRad), startY = cy + radius * Math.Sin(startRad);
            double endX = cx + radius * Math.Cos(endRad), endY = cy + radius * Math.Sin(endRad);
            int isLargeArc = sweepAngle > 180 ? 1 : 0;

            string data = string.Format(CultureInfo.InvariantCulture, "M {0:F3},{1:F3} A {2},{2} 0 {3} 1 {4:F3},{5:F3}", startX, startY, radius, isLargeArc, endX, endY);
            pathControl.Data = Geometry.Parse(data);
        }

        // ==========================================
        // 3. STATISTIQUES MATÉRIELLES (Avec Couleurs & Anti-Freeze)
        // ==========================================

        private bool _isStatsPolling = false; // Cadenas anti-freeze pour les jauges

        private void StartStatsWatcher()
        {
            _statsWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _statsWatcherTimer.Tick += async (sender, args) =>
            {
                if (_isStatsPolling) return; // Si ADB n'a pas fini la requête précédente, on zappe ce tour

                // Jauges invisibles (autre onglet, fenêtre réduite) : inutile d'interroger le terminal toutes les
                // 3 s. Le relevé reprend dès le retour sur le Dashboard.
                if (MainTabControl?.SelectedIndex != 0 || WindowState == WindowState.Minimized) return;
                _isStatsPolling = true;

                var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
                if (selectedDevice != null && selectedDevice.State == "device")
                {
                    string serial = selectedDevice.Serial;

                    // Exécution en arrière-plan pour ne PAS figer l'interface WPF
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // 1. UN SEUL appel adb pour les mesures dynamiques (auparavant 7 process adb.exe toutes les
                            // 3 s, soit ~140 par minute). Les infos fixes (ABI, puce, nombre de cœurs) ne sont lues
                            // qu'une fois par terminal puis gardées en cache.
                            var dynamic = await _adbManager.RunShellSectionsAsync(serial, null,
                                "dumpsys battery", "cat /proc/meminfo", "dumpsys cpuinfo", "df -h /data");
                            var hw = await GetStaticHardwareInfoAsync(serial);

                            AdbCommandResult Section(string output) =>
                                new AdbCommandResult { StandardOutput = output ?? "", ExitCode = string.IsNullOrEmpty(output) ? 1 : 0 };

                            var batTask = Task.FromResult(Section(dynamic[0]));
                            var ramTask = Task.FromResult(Section(dynamic[1]));
                            var cpuTask = Task.FromResult(Section(dynamic[2]));
                            var storageTask = Task.FromResult(Section(dynamic[3]));
                            var abiTask = Task.FromResult(Section(hw[0]));
                            var hwTask = Task.FromResult(Section(hw[1]));
                            // /proc/cpuinfo est une interface noyau Linux standard : fiable sur toutes marques/versions,
                            // contrairement à un nombre de cœurs codé en dur qui serait faux sur tout appareil non-octa-core.
                            var coresTask = Task.FromResult(Section(hw[2]));

                            // 2. PALETTE PARTAGÉE (déjà "freezée" une seule fois, voir champs statiques de MainWindow)
                            var greenBrush = BrushSuccess;
                            var orangeBrush = BrushWarning;
                            var redBrush = BrushDanger;
                            var defaultBrush = BrushAccent;

                            // --- BATTERIE ---
                            double batLevel = 0, batTemp = 0; int batVolt = 0;
                            string statusStr = "Inconnu", healthStr = "Inconnue", techStr = "Li-ion";

                            if (batTask.Result.IsSuccess)
                            {
                                string outBat = batTask.Result.StandardOutput;
                                var mLevel = Regex.Match(outBat, @"level:\s*(\d+)");
                                var mTemp = Regex.Match(outBat, @"temperature:\s*(\d+)");
                                var mVolt = Regex.Match(outBat, @"voltage:\s*(\d+)");
                                var mStatus = Regex.Match(outBat, @"status:\s*(\d+)");
                                var mHealth = Regex.Match(outBat, @"health:\s*(\d+)");
                                var mTech = Regex.Match(outBat, @"technology:\s*(.+)");

                                batLevel = mLevel.Success ? double.Parse(mLevel.Groups[1].Value) : 0;
                                batTemp = mTemp.Success ? double.Parse(mTemp.Groups[1].Value) / 10.0 : 0;
                                batVolt = mVolt.Success ? int.Parse(mVolt.Groups[1].Value) : 0;
                                statusStr = GetBatteryStatusText(mStatus.Success ? int.Parse(mStatus.Groups[1].Value) : 1);
                                healthStr = GetBatteryHealthText(mHealth.Success ? int.Parse(mHealth.Groups[1].Value) : 1);
                                techStr = mTech.Success ? mTech.Groups[1].Value.Trim() : "Li-ion";
                            }

                            // Logique Couleur Batterie
                            Brush batColor = defaultBrush;
                            if (batLevel <= 25) batColor = redBrush;
                            else if (batLevel >= 99) batColor = greenBrush;

                            // --- RAM ---
                            double ramPct = 0, totalRam = 0, usedRam = 0, availRam = 0, buffersMB = 0, swapMB = 0;
                            if (ramTask.Result.IsSuccess)
                            {
                                string outRam = ramTask.Result.StandardOutput;
                                var totMatch = Regex.Match(outRam, @"MemTotal:\s+(\d+)");
                                var avMatch = Regex.Match(outRam, @"MemAvailable:\s+(\d+)");
                                var bufMatch = Regex.Match(outRam, @"Buffers:\s+(\d+)");
                                var swapMatch = Regex.Match(outRam, @"SwapTotal:\s+(\d+)");

                                if (totMatch.Success && avMatch.Success)
                                {
                                    totalRam = double.Parse(totMatch.Groups[1].Value) / 1024.0 / 1024.0;
                                    availRam = double.Parse(avMatch.Groups[1].Value) / 1024.0 / 1024.0;
                                    usedRam = totalRam - availRam;
                                    ramPct = (usedRam / totalRam) * 100.0;
                                    buffersMB = bufMatch.Success ? double.Parse(bufMatch.Groups[1].Value) / 1024.0 : 0;
                                    swapMB = swapMatch.Success ? double.Parse(swapMatch.Groups[1].Value) / 1024.0 : 0;
                                }
                            }

                            // Logique Couleur RAM
                            Brush ramColor = greenBrush;
                            if (ramPct >= 85) ramColor = redBrush;
                            else if (ramPct >= 70) ramColor = orangeBrush;

                            // --- CPU ---
                            double cpuLevel = 0;
                            string abi = "arm64-v8a", hardware = "Qualcomm/Exynos";
                            if (cpuTask.Result.IsSuccess)
                            {
                                var match = Regex.Match(cpuTask.Result.StandardOutput, @"(\d+[\.,]?\d*)%\s+TOTAL");
                                cpuLevel = (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double val)) ? val : 0;
                            }
                            if (abiTask.Result.IsSuccess) abi = abiTask.Result.StandardOutput.Trim();
                            if (hwTask.Result.IsSuccess) hardware = hwTask.Result.StandardOutput.Trim();

                            // Nombre de cœurs réel (compte les lignes "processor" de /proc/cpuinfo, universel Linux/Android)
                            int coreCount = coresTask.Result.IsSuccess
                                ? Regex.Matches(coresTask.Result.StandardOutput, @"(?m)^processor\s*:\s*\d+").Count
                                : 0;

                            // Logique Couleur CPU
                            Brush cpuColor = greenBrush;
                            if (cpuLevel >= 85) cpuColor = redBrush;
                            else if (cpuLevel >= 60) cpuColor = orangeBrush;

                            // --- STOCKAGE (même parsing que le test SAV "storage", voir DiagnosticService) ---
                            int storagePct = 0;
                            string storageTotal = "--", storageUsed = "--", storageFree = "--";
                            if (storageTask.Result.IsSuccess)
                            {
                                var storageValues = DiagnosticService.ParseStorageValues(storageTask.Result.StandardOutput);
                                if (storageValues != null)
                                {
                                    storageTotal = storageValues.Value.Total;
                                    storageUsed = storageValues.Value.Used;
                                    storageFree = storageValues.Value.Free;
                                    storagePct = storageValues.Value.UsedPercent;
                                }
                            }

                            // Logique Couleur Stockage
                            Brush storageColor = greenBrush;
                            if (storagePct >= 95) storageColor = redBrush;
                            else if (storagePct >= 85) storageColor = orangeBrush;

                            // 3. MISE À JOUR DE L'INTERFACE GRAPHIQUE (UI)
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                // On force le texte en blanc
                                TxtBatteryPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                                TxtRamPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                                TxtCpuPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

                                // --- Batterie UI ---
                                TxtBatteryPct.Text = $"{batLevel:F0}%";
                                PathBatteryProgress.Stroke = batColor; // Seulement la jauge change de couleur
                                GlowBattery.Color = ((SolidColorBrush)batColor).Color; // La lueur de la carte suit le même statut
                                TxtBatteryDetails.Text = $"{batVolt} mV • {batTemp:F1}°C";
                                UpdateGaugePath(PathBatteryProgress, batLevel);

                                TxtPopupBatLevel.Text = Loc.F("Niveau : {0}%", batLevel);
                                TxtPopupBatStatus.Text = Loc.F("Statut : {0}", statusStr);
                                TxtPopupBatHealth.Text = Loc.F("Santé : {0}", healthStr);
                                TxtPopupBatTemp.Text = Loc.F("Température : {0:F1} °C", batTemp);
                                TxtPopupBatVoltage.Text = Loc.F("Tension : {0} mV", batVolt);
                                TxtPopupBatTech.Text = Loc.F("Technologie : {0}", techStr);

                                // --- RAM UI ---
                                TxtRamPct.Text = $"{ramPct:F0}%";
                                PathRamProgress.Stroke = ramColor; // Seulement la jauge change de couleur
                                GlowRam.Color = ((SolidColorBrush)ramColor).Color; // La lueur de la carte suit le même statut
                                TxtRamDetails.Text = $"{usedRam:F1} / {totalRam:F1} GB";
                                UpdateGaugePath(PathRamProgress, ramPct);

                                TxtPopupRamTotal.Text = Loc.F("RAM Totale : {0:F2} GB", totalRam);
                                TxtPopupRamUsed.Text = Loc.F("Utilisée : {0:F2} GB", usedRam);
                                TxtPopupRamFree.Text = Loc.F("Disponible : {0:F2} GB", availRam);
                                TxtPopupRamBuffers.Text = Loc.F("Buffers / Cache : {0:F0} MB", buffersMB);
                                TxtPopupRamSwap.Text = Loc.F("Swap / ZRAM : {0:F0} MB", swapMB);

                                // --- CPU UI ---
                                TxtCpuPct.Text = $"{cpuLevel:F0}%";
                                PathCpuProgress.Stroke = cpuColor; // Seulement la jauge change de couleur
                                GlowCpu.Color = ((SolidColorBrush)cpuColor).Color; // La lueur de la carte suit le même statut
                                TxtCpuDetails.Text = coreCount > 0 ? Loc.F("{0} Cœurs • Charge", coreCount) : Loc.T("-- Cœurs • Charge");
                                UpdateGaugePath(PathCpuProgress, cpuLevel);

                                TxtPopupCpuUsage.Text = Loc.F("Charge globale : {0:F1}%", cpuLevel);
                                TxtPopupCpuCores.Text = coreCount > 0
                                    ? Loc.F("Nombre de Cœurs : {0} ({1})", coreCount, GetCoreCountLabel(coreCount))
                                    : Loc.T("Nombre de Cœurs : Inconnu");
                                TxtPopupCpuArch.Text = Loc.F("Architecture : {0}", abi);
                                TxtPopupCpuHardware.Text = Loc.F("Puce / Hardware : {0}", hardware);

                                // --- Stockage UI ---
                                TxtStoragePct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                                TxtStoragePct.Text = $"{storagePct}%";
                                PathStorageProgress.Stroke = storageColor;
                                GlowStorage.Color = ((SolidColorBrush)storageColor).Color; // La lueur de la carte suit le même statut
                                TxtStorageDetails.Text = $"{storageUsed} / {storageTotal}";
                                UpdateGaugePath(PathStorageProgress, storagePct);

                                TxtPopupStorageTotal.Text = Loc.F("Espace Total : {0}", storageTotal);
                                TxtPopupStorageUsed.Text = Loc.F("Utilisé : {0} ({1}%)", storageUsed, storagePct);
                                TxtPopupStorageFree.Text = Loc.F("Disponible : {0}", storageFree);
                            });
                        }
                        catch { }
                        finally
                        {
                            // Quoi qu'il arrive, on libère le cadenas pour le prochain cycle
                            _isStatsPolling = false;
                        }
                    });
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        // Remise à zéro propre si aucun terminal
                        var greyBrush = BrushNeutral;

                        // Jauges en gris, Texte en blanc
                        PathBatteryProgress.Stroke = greyBrush; TxtBatteryPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                        PathRamProgress.Stroke = greyBrush; TxtRamPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                        PathCpuProgress.Stroke = greyBrush; TxtCpuPct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                        PathStorageProgress.Stroke = greyBrush; TxtStoragePct.SetResourceReference(TextBlock.ForegroundProperty, "Ink");

                        UpdateGaugePath(PathBatteryProgress, 0); TxtBatteryPct.Text = "--%"; TxtBatteryDetails.Text = "-- mV • --°C";
                        UpdateGaugePath(PathRamProgress, 0); TxtRamPct.Text = "--%"; TxtRamDetails.Text = "-- / -- GB";
                        UpdateGaugePath(PathCpuProgress, 0); TxtCpuPct.Text = "--%"; TxtCpuDetails.Text = Loc.T("-- Cœurs • Charge");
                        UpdateGaugePath(PathStorageProgress, 0); TxtStoragePct.Text = "--%"; TxtStorageDetails.Text = "-- / -- Go";
                    });
                    _isStatsPolling = false;
                }
            };
            _statsWatcherTimer.Start();
        }

        // Infos matérielles fixes par terminal (ABI, puce, /proc/cpuinfo) : lues une seule fois.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> _staticHardwareInfo =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string[]>();

        private async Task<string[]> GetStaticHardwareInfoAsync(string serial)
        {
            if (_staticHardwareInfo.TryGetValue(serial, out var cached)) return cached;

            var info = await _adbManager.RunShellSectionsAsync(serial, null,
                "getprop ro.product.cpu.abi", "getprop ro.hardware", "cat /proc/cpuinfo");

            // On ne met en cache qu'une lecture complète (terminal pas encore prêt = on réessaiera).
            if (!string.IsNullOrEmpty(info[0])) _staticHardwareInfo[serial] = info;
            return info;
        }

        private static string GetCoreCountLabel(int count)
        {
            switch (count)
            {
                case 1: return "Mono-Core";
                case 2: return "Dual-Core";
                case 4: return "Quad-Core";
                case 6: return "Hexa-Core";
                case 8: return "Octa-Core";
                case 10: return "Deca-Core";
                default: return $"{count}-Core";
            }
        }

        private void BtnBatteryDetails_Click(object sender, RoutedEventArgs e) => PopupBattery.IsOpen = !PopupBattery.IsOpen;
        private void BtnRamDetails_Click(object sender, RoutedEventArgs e) => PopupRam.IsOpen = !PopupRam.IsOpen;
        private void BtnCpuDetails_Click(object sender, RoutedEventArgs e) => PopupCpu.IsOpen = !PopupCpu.IsOpen;
        private void BtnStorageDetails_Click(object sender, RoutedEventArgs e) => PopupStorage.IsOpen = !PopupStorage.IsOpen;

        // Table commune avec le check-up SAV (Core/DiagnosticService.cs → BatteryLabels). L'ancienne table du
        // Dashboard affichait "Normale" pour Inconnue (1), Défaillance (6) et Trop froide (7).
        private string GetBatteryStatusText(int status) => BatteryLabels.Status(status);

        private string GetBatteryHealthText(int health) => BatteryLabels.Health(health);


        // ==========================================
        // ACTIONS RAPIDES : DEV OFF, RELOAD, RECOVERY
        // ==========================================
        private async void BtnDevOff_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || selectedDevice.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Aucun appareil valide sélectionné.", DateTime.Now));
                return;
            }

            bool confirm = await ShowCustomDialogAsync(Loc.T("🔒 Dev Off"), Loc.F("Désactiver le mode développeur et le débogage USB sur {0} ?\n\nLa connexion ADB sera immédiatement rompue — il faudra réactiver le débogage USB manuellement sur le terminal pour s'y reconnecter.", selectedDevice.Serial), Loc.T("Désactiver"), Loc.T("Annuler"), true);
            if (!confirm) return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🛡️ Désactivation du mode développeur sur {1}...", DateTime.Now, selectedDevice.Serial));

            try
            {
                // LA DOUBLE FRAPPE : On cache le menu ET on coupe le processus ADB directement.
                // On utilise ";" pour enchaîner les deux commandes dans le même shell avant que la connexion ne coupe.
                var result = await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} shell \"settings put global development_settings_enabled 0 ; settings put global adb_enabled 0\"");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // On ne se fie pas forcément à result.IsSuccess ici. 
                    // Couper l'ADB depuis l'ADB provoque une coupure violente (Broken Pipe / EOF) que le C# perçoit parfois comme une erreur.
                    // Si on arrive ici sans crash majeur, c'est que la roquette a bien été tirée !

                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Mode développeur et Débogage USB désactivés de force. (Connexion rompue).", DateTime.Now));

                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [INFO] La connexion a été coupée par le terminal : {1}", DateTime.Now, ex.Message));
                });
            }
        }

        private async void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || selectedDevice.State != "device") return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 🔄 Redémarrage en cours ({1})...", DateTime.Now, selectedDevice.Serial));

            try
            {
                await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} reboot");
            }
            catch { }
        }

        private async void BtnRecovery_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || selectedDevice.State != "device") return;

            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚕️ Passage en mode Recovery ({1})...", DateTime.Now, selectedDevice.Serial));

            try
            {
                await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} reboot recovery");
            }
            catch { }
        }

        // ==========================================
        // MODULE WI-FI PAIRING
        // ==========================================
        private async void BtnWifiLegacy_Click(object sender, RoutedEventArgs e)
        {
            var selectedDevice = DeviceComboBox.SelectedItem as AndroidDevice;
            if (selectedDevice == null || selectedDevice.State != "device")
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez sélectionner un appareil connecté en USB pour basculer en Wi-Fi.", DateTime.Now));
                return;
            }

            BtnWifiLegacy.IsEnabled = false;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📡 Recherche de l'IP du terminal {1}...", DateTime.Now, selectedDevice.Serial));

            try
            {
                var ipResult = await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} shell ip route");

                string ipAddress = "";
                var match = Regex.Match(ipResult.StandardOutput, @"src\s+([0-9\.]+)");
                if (match.Success) ipAddress = match.Groups[1].Value;

                if (string.IsNullOrEmpty(ipAddress))
                {
                    Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ❌ Aucune adresse IP Wi-Fi trouvée. Le terminal est-il connecté au réseau ?", DateTime.Now)); });
                    return;
                }

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📡 IP trouvée : {1}. Ouverture du port TCP 5555...", DateTime.Now, ipAddress));

                await _adbManager.ExecuteAsync($"-s {selectedDevice.Serial} tcpip 5555");
                await System.Threading.Tasks.Task.Delay(2000);

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📡 Connexion sans fil à {1}:5555...", DateTime.Now, ipAddress));

                var connectResult = await _adbManager.ExecuteAsync($"connect {ipAddress}:5555");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Résultat : {1}", DateTime.Now, connectResult.StandardOutput.Trim()));
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 💡 Vous pouvez maintenant débrancher le câble USB !", DateTime.Now));
                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [EXCEPTION] {1}", DateTime.Now, ex.Message)); });
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => { BtnWifiLegacy.IsEnabled = true; });
            }
        }

        private async void BtnWifiModern_Click(object sender, RoutedEventArgs e)
        {
            string pairIpPort = TxtWifiPairIp.Text.Trim();
            string pairCode = TxtWifiPairCode.Text.Trim();
            string connectPort = TxtWifiConnectPort.Text.Trim();

            if (string.IsNullOrEmpty(pairIpPort) || string.IsNullOrEmpty(pairCode) || string.IsNullOrEmpty(connectPort))
            {
                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ⚠️ Veuillez remplir les 3 champs (IP:Port de pairage, Code, et Port de connexion).", DateTime.Now));
                return;
            }

            string pureIp = pairIpPort.Split(':')[0];

            BtnWifiModern.IsEnabled = false;
            AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📡 [ÉTAPE 1/2] Tentative de Pairage avec {1}...", DateTime.Now, pairIpPort));

            try
            {
                var pairResult = await _adbManager.ExecuteAsync($"pair {pairIpPort} {pairCode}");
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] Retour Pairage : {1}{2}", DateTime.Now, pairResult.StandardOutput.Trim(), pairResult.StandardError.Trim())); });

                await System.Threading.Tasks.Task.Delay(1000);

                AppendConsole(Loc.F("\n[{0:HH:mm:ss}] 📡 [ÉTAPE 2/2] Connexion finale à {1}:{2}...", DateTime.Now, pureIp, connectPort));
                var connectResult = await _adbManager.ExecuteAsync($"connect {pureIp}:{connectPort}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    AppendConsole(Loc.F("\n[{0:HH:mm:ss}] ✅ Résultat Connexion : {1}", DateTime.Now, connectResult.StandardOutput.Trim()));
                    if (ConsoleOutput.Parent is System.Windows.Controls.ScrollViewer scroll) scroll.ScrollToEnd();
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { AppendConsole(Loc.F("\n[{0:HH:mm:ss}] [EXCEPTION] {1}", DateTime.Now, ex.Message)); });
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => { BtnWifiModern.IsEnabled = true; });
            }
        }
    }
}
