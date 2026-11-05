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
        // 📡 MOTEUR RADAR WI-FI & SNIFFER
        // ==========================================

        private readonly RadarService _radarService;
        private DispatcherTimer _radarTimer;
        public ChartValues<int> PingValues { get; set; }
        public ChartValues<int> RssiValues { get; set; }

        // Toute la session, pour les graphiques du rapport Word (l'écran n'affiche que les 40 derniers points).
        // Ping perdu = null. Plafond de sécurité pour les très longues sessions.
        private readonly List<int?> _pingHistory = new List<int?>();
        private readonly List<int> _rssiHistory = new List<int>();
        private const int ReportHistoryLimit = 20000;

        private bool _isPolling = false; // Le cadenas anti-embouteillage
        private string _lastBssid = ""; // Mémoire de la dernière antenne connue
        private bool _isFetchingHeavyData = false; // Cadenas pour les infos lourdes (IP/DNS)

        // Incrémenté à chaque démarrage/arrêt du Radar : permet à une requête MAC/IP/passerelle/DNS lancée
        // en arrière-plan (orpheline, non annulable) de détecter que sa session s'est terminée entre-temps
        // et de ne pas écraser l'UI avec des données périmées après un Arrêt ou une Réinitialisation.
        private int _radarSessionId = 0;
        private async void BtnToggleRadar_Click(object sender, RoutedEventArgs e)
        {
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            if (dev == null || dev.State != "device")
            {
                await ShowCustomDialogAsync("❌ ERREUR", "Veuillez connecter un terminal avant de lancer le Radar.", "OK", null, false);
                return;
            }

            if (_radarService.IsRunning)
            {
                StopRadar();
            }
            else
            {
                // DÉTECTION DU MODE DE CONNEXION (USB ou WI-FI)
                // Si le "Serial" contient un point et deux points (ex: 192.168.1.20:5555), c'est du TCP/IP (Wi-Fi)
                if (dev.Serial.Contains(".") && dev.Serial.Contains(":"))
                {
                    // Affichage de ta pop-up d'avertissement avec la DA du logiciel
                    bool continueRadar = await ShowCustomDialogAsync(
                        "⚠️ AVERTISSEMENT WI-FI",
                        "Vous auditez le Wi-Fi via une connexion Wi-Fi (ADB sans fil).\n\nEn cas de coupure réseau, l'analyse Wi-Fi sera déconnectée et l'analyse s'arrêtera.\n\nPour un audit de mobilité complet (Roaming), privilégiez une connexion USB.",
                        "Continuer l'analyse",
                        "Annuler",
                        true
                    );

                    // Si l'utilisateur clique sur "Annuler", on ne lance pas le radar
                    if (!continueRadar)
                    {
                        return;
                    }
                }

                // Si c'est de l'USB, ou si l'utilisateur a cliqué sur "Continuer", on lance le moteur
                // Heure du terminal au lancement : le logcat Wi-Fi ne lira que les événements à partir de là.
                string logcatSince = await _radarService.GetDeviceLogTimeAsync(dev.Serial);
                StartRadar(dev.Serial, logcatSince);
            }
        }

        // Terminal audité par la dernière session Radar : les exports l'utilisent plutôt que la sélection
        // courante (qui peut avoir changé, ou être vide si le terminal a été débranché après l'audit).
        private string _radarSerial;

        /// <summary>Serial à inscrire dans les rapports Radar ; null si aucun audit n'a été lancé et aucun terminal valide n'est sélectionné.</summary>
        private string GetRadarReportSerial()
        {
            if (!string.IsNullOrEmpty(_radarSerial)) return _radarSerial;
            var dev = DeviceComboBox.SelectedItem as AndroidDevice;
            return dev != null && dev.State == "device" ? dev.Serial : null;
        }

        private void StartRadar(string serial, string logcatSince = null)
        {
            _radarSerial = serial;
            _radarSessionId++;
            BtnToggleRadar.Content = "⏹ ARRÊTER";
            BtnToggleRadar.ClearValue(Button.BackgroundProperty);
            AnimateNavButton(BtnNavRadar, true);

            // Initialisation du Graphique (les statistiques sont réinitialisées par _radarService.Start ci-dessous)
            PingValues = new ChartValues<int>();
            PingLineSeries.Values = PingValues;
            RssiValues = new ChartValues<int>();
            RssiLineSeries.Values = RssiValues;
            _lastBssid = "";
            _isFetchingHeavyData = false;
            _isPolling = false;

            TxtWifiLogcat.Text = "--- Début du Sniffing Wi-Fi ---\n";

            // 1. MOTEUR POLLING (Ultra-Rapide & Découplé)
            _radarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0) };
            _radarTimer.Tick += async (s, ev) =>
            {
                if (_isPolling) return; // Sécurité anti-spam
                _isPolling = true;

                try
                {
                    var snapshot = await _radarService.PollWifiStatusAsync(serial);

                    if (snapshot.IsConnected)
                    {
                        // MISE À JOUR INSTANTANÉE DES JAUGES
                        TxtWifiSsid.Text = snapshot.Ssid;
                        TxtWifiBssid.Text = $"BSSID : {snapshot.Bssid}";

                        if (snapshot.Rssi.HasValue)
                        {
                            int rssiVal = snapshot.Rssi.Value;
                            TxtWifiRssi.Text = $"{rssiVal} dBm";
                            if (rssiVal >= -65) TxtWifiRssi.Foreground = BrushSuccess;
                            else if (rssiVal >= -75) TxtWifiRssi.Foreground = BrushWarning;
                            else TxtWifiRssi.Foreground = BrushDanger;

                            RssiValues.Add(rssiVal);
                            if (RssiValues.Count > 40) RssiValues.RemoveAt(0);
                            if (_rssiHistory.Count < ReportHistoryLimit) _rssiHistory.Add(rssiVal);

                            var rStats = _radarService.Stats;
                            TxtRssiMinMaxAvg.Text = $"Min/Moy/Max : {rStats.MinRssiText} / {rStats.AverageRssi} / {rStats.MaxRssiText} dBm";
                        }

                        TxtWifiSpeed.Text = snapshot.LinkSpeed;

                        if (!string.IsNullOrEmpty(snapshot.Band))
                        {
                            TxtWifiFreq.Text = $"Bande : {snapshot.Band} / Canal : {snapshot.Channel}";
                        }

                        // --- LE DÉCLENCHEUR DÉCOUPLÉ (Fini les freezes de 20 sec !) ---
                        // Si le BSSID a changé (nouvelle connexion ou roaming), on lance la requête lourde en fond
                        if (snapshot.Bssid != _lastBssid && !_isFetchingHeavyData)
                        {
                            _lastBssid = snapshot.Bssid;
                            _isFetchingHeavyData = true;

                            TxtWifiIp.Text = "IP : Récupération...";
                            TxtWifiGateway.Text = "Passerelle : Récupération...";
                            TxtWifiDns.Text = "DNS : Récupération...";

                            // Processus orphelin (Ne bloque pas le timer principal)
                            int fetchSessionId = _radarSessionId;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var macTask = _radarService.GetDeviceMacAddressAsync(serial, snapshot.RawDumpsysWifi);
                                    var ipTask = _adbManager.ExecuteAsync($"-s {serial} shell ip addr show wlan0");
                                    var gwTask = _radarService.GetDeviceGatewayAsync(serial);
                                    var dnsTask = _radarService.GetDeviceDnsAsync(serial);

                                    await Task.WhenAll(macTask, ipTask, gwTask, dnsTask);

                                    // Retour sur le thread UI pour mettre à jour l'affichage — sauf si la
                                    // session a changé entre-temps (Arrêt/Réinitialisation), auquel cas ces
                                    // données seraient périmées et écraseraient l'UI à tort.
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        if (fetchSessionId != _radarSessionId) return;

                                        TxtWifiMac.Text = $"MAC : {macTask.Result}";
                                        var mIp = System.Text.RegularExpressions.Regex.Match(ipTask.Result.StandardOutput ?? "", @"inet\s+([0-9\.]+)/\d+");
                                        TxtWifiIp.Text = mIp.Success ? $"IP : {mIp.Groups[1].Value}" : "IP : ---.---.---.---";
                                        TxtWifiGateway.Text = $"Passerelle : {gwTask.Result}";
                                        TxtWifiDns.Text = $"DNS : {dnsTask.Result}";
                                    });
                                }
                                catch { }
                                finally { _isFetchingHeavyData = false; }
                            });
                        }
                    }
                    else
                    {
                        // DÉCONNECTÉ
                        _lastBssid = ""; // On réinitialise pour forcer la récup au retour du Wi-Fi
                        TxtWifiSsid.Text = "Déconnecté";
                        TxtWifiBssid.Text = "BSSID : --:--:--:--:--:--";
                        TxtWifiRssi.Text = "- dBm";
                        TxtWifiRssi.Foreground = BrushDanger;
                        TxtWifiSpeed.Text = "- Mbps";
                        TxtWifiFreq.Text = "Bande : -- / Canal : --";

                        TxtWifiIp.Text = "IP : ---.---.---.---";
                        TxtWifiGateway.Text = "Passerelle : ---.---.---.---";
                        TxtWifiDns.Text = "DNS : ---.---.---.---";
                    }
                }
                catch { }
                finally
                {
                    _isPolling = false; // On libère le timer coûte que coûte
                }
            };
            _radarTimer.Start();

            // 2. MOTEUR PING CONTINU + 3. MOTEUR LOGCAT (Avec Comptage des Déconnexions)
            _radarService.Start(_adbExePath, serial,
                onPingSample: sample =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        PingValues.Add(sample.LatencyMs);
                        if (PingValues.Count > 40) PingValues.RemoveAt(0);
                        if (_pingHistory.Count < ReportHistoryLimit) _pingHistory.Add(sample.Success ? (int?)sample.LatencyMs : null);
                        // Échelle qui suit les mesures : un pic au-delà de 200 ms n'est plus coupé.
                        if (PingChart.AxisY.Count > 0)
                        {
                            int peak = PingValues.Count > 0 ? PingValues.Max() : 0;
                            PingChart.AxisY[0].MaxValue = Math.Max(200, Math.Ceiling(peak * 1.1 / 50.0) * 50);
                        }

                        if (sample.Success)
                        {
                            // --- MISE À JOUR LATENCE TEMPS RÉEL ---
                            TxtPingLatency.Text = $"Latence : {sample.LatencyMs} ms";
                            if (sample.LatencyMs <= 50) TxtPingLatency.Foreground = BrushSuccess; // Vert
                            else if (sample.LatencyMs <= 100) TxtPingLatency.Foreground = BrushWarning; // Jaune/Orange
                            else TxtPingLatency.Foreground = BrushDanger; // Rouge (Lag)
                        }
                        else
                        {
                            // --- LATENCE EN ERREUR ---
                            TxtPingLatency.Text = "Latence : Échec";
                            TxtPingLatency.Foreground = BrushDanger; // Rouge
                        }

                        if (_radarService.Stats.PingTotal > 0)
                        {
                            // --- MISE À JOUR DU COMPTEUR ---
                            var pStats = _radarService.Stats;
                            TxtPingCount.Text = $"Envoyés : {pStats.PingTotal}";

                            double lossPercent = pStats.LossPercent;
                            TxtPacketLoss.Text = $"Perte : {lossPercent} %";
                            TxtPacketLoss.Foreground = lossPercent > 5 ? BrushDanger : BrushSuccess;

                            TxtPingMinMaxAvg.Text = $"Min/Moy/Max : {pStats.MinPingText} / {pStats.AveragePingMs} / {pStats.MaxPingText} ms";
                        }
                    });
                },
                onWifiEventLine: line =>
                {
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 1. On écrit dans la console noire (AppendText : pas de recopie de tout le texte à
                        // chaque ligne ; plafonné pour qu'une longue session ne ralentisse pas l'affichage).
                        AppendWifiLogcatLine(line);

                        // 2. LE PONT : Si on capte une coupure en direct, on force les jauges à "Déconnecté" immédiatement !
                        if (line.Contains("DISCONNECTED"))
                        {
                            TxtWifiSsid.Text = "Micro-coupure...";
                            TxtWifiRssi.Text = "- dBm";
                            TxtWifiRssi.Foreground = BrushDanger; // Rouge flash
                            TxtWifiSpeed.Text = "- Mbps";
                        }

                        // 3. Si on capte une reconnexion, on remet "Connexion en cours..."
                        if (line.Contains("CONNECTED") && !line.Contains("DISCONNECTED"))
                        {
                            TxtWifiSsid.Text = "Reconnexion...";
                            TxtWifiRssi.Foreground = BrushWarning; // Orange
                        }
                    });
                }, logcatSince);
        }

        // Au-delà, on ne garde que la fin du journal affiché (le rapport reprend ce texte).
        private const int WifiLogcatMaxChars = 200000;

        private void AppendWifiLogcatLine(string line)
        {
            TxtWifiLogcat.AppendText(line + "\n");
            if (TxtWifiLogcat.Text.Length > WifiLogcatMaxChars)
            {
                string text = TxtWifiLogcat.Text;
                int cut = text.IndexOf('\n', text.Length - WifiLogcatMaxChars * 3 / 4);
                TxtWifiLogcat.Text = "[... début du journal tronqué ...]\n" + (cut >= 0 ? text.Substring(cut + 1) : text);
            }
            TxtWifiLogcat.ScrollToEnd();
        }

        private void StopRadar()
        {
            _radarSessionId++;
            BtnToggleRadar.Content = "▶ DÉMARRER";
            BtnToggleRadar.ClearValue(Button.BackgroundProperty);
            AnimateNavButton(BtnNavRadar, false);

            if (_radarTimer != null) { _radarTimer.Stop(); _radarTimer = null; }

            _radarService.Stop();

            TxtWifiLogcat.Text += "\n--- Sniffing Arrêté ---\n";
            TxtWifiLogcat.ScrollToEnd();
        }

        // ==========================================
        // EXPORT DU RAPPORT
        // ==========================================

        /// <summary>"Marque Modèle" du terminal (ex. "Zebra TC53"), même libellé que le rapport SAV.
        /// Un seul appel adb pour les deux propriétés.</summary>
        private async Task<string> GetBrandModelAsync(string serial)
        {
            var props = await _adbManager.RunShellSectionsAsync(serial, null, "getprop ro.product.brand", "getprop ro.product.model");
            string label = DiagnosticService.FormatBrandModel(props[0], props[1]);
            return string.IsNullOrEmpty(label) || label.Contains("error") ? "Terminal_Android" : label;
        }

        private async void BtnExportRadarReport_Click(object sender, RoutedEventArgs e)
        {
            string serial = GetRadarReportSerial();
            if (serial == null)
            {
                await ShowCustomDialogAsync("❌ ERREUR", "Aucun terminal connecté pour générer un rapport.", "OK", null, false);
                return;
            }

            try
            {
                string deviceModel = await GetBrandModelAsync(serial);

                // Calculs des moyennes de l'audit
                var stats = _radarService.Stats;
                TimeSpan testDuration = stats.Duration;

                StringBuilder report = new StringBuilder();
                report.AppendLine("=================================================");
                report.AppendLine("     RAPPORT D'EXPERTISE RÉSEAU (ANALYSE WIFI)   ");
                report.AppendLine("=================================================");
                report.AppendLine($"Date de l'analyse : {DateTime.Now:dd/MM/yyyy à HH:mm:ss}");
                report.AppendLine($"Terminal ciblé    : {deviceModel} (S/N: {serial})");
                report.AppendLine($"Durée du test     : {RadarStats.FormatDuration(testDuration)}");
                report.AppendLine("=================================================\n");

                report.AppendLine("[ 1. RÉSUMÉ DES PERFORMANCES (BILAN GLOBAL) ]");
                report.AppendLine($"Qualité Signal (RSSI) : Moyenne {stats.AverageRssi} dBm | Pire {stats.MinRssiText} dBm | Meilleur {stats.MaxRssiText} dBm");
                report.AppendLine($"Latence Réseau (Ping) : Moyenne {stats.AveragePingMs} ms | Max {stats.MaxPingText} ms | Min {stats.MinPingText} ms");
                report.AppendLine($"Sauts d'itinérance    : {stats.RoamingCount} événement(s) de changement d'antenne");
                report.AppendLine($"Micro-coupures / Déco : {stats.DisconnectCount} déconnexion(s) détectée(s)");

                report.AppendLine($"Taux de paquets perdus: {stats.LossPercent} % ({stats.PingLost} sur {stats.PingTotal} envoyés)");

                // Même verdict que le rapport Word (calculé dans RadarStats), avec une explication en plus ici.
                string diag;
                switch (stats.Verdict)
                {
                    case RadarVerdict.Critique: diag = "CRITIQUE (Réseau encombré, roaming instable ou trous de couverture)"; break;
                    case RadarVerdict.Moyen: diag = "MOYEN (Micro-coupures, ou point d'accès saturé)"; break;
                    default: diag = "EXCELLENT"; break;
                }
                report.AppendLine($"\n>>> DIAGNOSTIC FINAL  : {diag} <<<\n");

                report.AppendLine("[ 2. CONFIGURATION DU TERMINAL ET ROUTAGE ]");
                report.AppendLine($"Dernier Réseau (SSID) : {TxtWifiSsid.Text}");
                report.AppendLine($"Dernière Antenne      : {TxtWifiBssid.Text.Replace("BSSID : ", "")}");
                report.AppendLine($"Fréquence / Canal     : {TxtWifiFreq.Text}");
                report.AppendLine($"Vitesse de Liaison    : {TxtWifiSpeed.Text}");
                report.AppendLine($"Adresse MAC Matérielle: {TxtWifiMac.Text.Replace("MAC : ", "")}");
                report.AppendLine($"Configuration IP      : {TxtWifiIp.Text.Replace("IP : ", "")}");
                report.AppendLine($"Passerelle (Gateway)  : {TxtWifiGateway.Text.Replace("Passerelle : ", "")}");
                report.AppendLine($"Serveur DNS primaire  : {TxtWifiDns.Text.Replace("DNS : ", "")}\n");

                report.AppendLine("[ 3. JOURNAL BRUT DES ÉVÉNEMENTS (LOGCAT) ]");
                report.AppendLine("-------------------------------------------------");

                string logcatText = TxtWifiLogcat.Text;
                if (string.IsNullOrWhiteSpace(logcatText) || logcatText == "--- Début du Sniffing Wi-Fi ---\n")
                {
                    report.AppendLine("Aucun événement réseau brut enregistré durant la session.");
                }
                else
                {
                    report.AppendLine(logcatText);
                }
                report.AppendLine("-------------------------------------------------");
                report.AppendLine("Fin du rapport.");

                string captureFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Captures");
                if (!Directory.Exists(captureFolder)) Directory.CreateDirectory(captureFolder);

                string safeModelName = ADBToolsNew.Core.FileNameHelper.Sanitize(deviceModel).Replace(' ', '_');
                string fileName = $"Rapport_Reseau_{safeModelName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string fullPath = Path.Combine(captureFolder, fileName);

                File.WriteAllText(fullPath, report.ToString());

                bool openFolder = await ShowCustomDialogAsync(
                    "💾 EXPORT RÉUSSI",
                    $"L'audit complet a été généré avec succès dans le dossier Captures :\n\n📄 {fileName}\n\nVoulez-vous l'ouvrir maintenant ?",
                    "📂 OUVRIR",
                    "Fermer",
                    true
                );

                if (openFolder)
                {
                    OpenFolder(captureFolder);
                }
            }
            catch (Exception ex)
            {
                await ShowCustomDialogAsync("❌ ERREUR", $"Erreur lors de la génération du rapport : {ex.Message}", "OK", null, false);
            }
        }

        private async void BtnExportRadarWordReport_Click(object sender, RoutedEventArgs e)
        {
            string serial = GetRadarReportSerial();
            if (serial == null)
            {
                await ShowCustomDialogAsync("❌ ERREUR", "Aucun terminal connecté pour générer un rapport.", "OK", null, false);
                return;
            }

            string templatePath = ADBToolsNew.Core.ReportTemplates.GetTemplatePath(ADBToolsNew.Core.ReportKind.Wifi); // modèle perso (Templates\) sinon modèle fourni
            AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📝 Modèle Word utilisé : " + (ADBToolsNew.Core.ReportTemplates.HasUserTemplate(ADBToolsNew.Core.ReportKind.Wifi) ? "personnel (" + System.IO.Path.GetFileName(templatePath) + ")" : "modèle fourni"));
            if (!System.IO.File.Exists(templatePath))
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ⚠️ Template Word introuvable : {templatePath}");
                return;
            }

            bool ok1 = await ShowCustomDialogAsync("📝 Export Word — Société", "Nom de la société / du client pour l'en-tête du rapport :", "Suivant", "Annuler", false, requiresInput: true, defaultInput: "");
            if (!ok1) return;
            string societe = TxtDialogInput.Text.Trim();

            bool ok2 = await ShowCustomDialogAsync("📝 Export Word — N° d'incident", "Numéro d'incident / de ticket pour l'en-tête du rapport :", "Générer le rapport", "Annuler", false, requiresInput: true, defaultInput: "");
            if (!ok2) return;
            string incident = TxtDialogInput.Text.Trim();

            try
            {
                string deviceModel = await GetBrandModelAsync(serial);

                // Graphiques dessinés pour l'impression (fond blanc, échelle adaptée, toute la session).
                string chartBase = System.IO.Path.Combine(GetCaptureFolderPath(), $"Diag_wifi_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}");
                string pingChartPath = _pingHistory.Count > 0 ? ADBToolsNew.Core.ReportChartRenderer.RenderPing(_pingHistory, chartBase + "_ping.png") : "";
                string rssiChartPath = _rssiHistory.Count > 0 ? ADBToolsNew.Core.ReportChartRenderer.RenderRssi(_rssiHistory, chartBase + "_rssi.png") : "";

                var stats = _radarService.Stats;
                TimeSpan duration = stats.Duration;

                string outputPath = ADBToolsNew.Core.WifiReportService.GenerateReport(
                    templatePath, GetReportsFolderPath(), serial, deviceModel, societe, incident,
                    TxtWifiSsid.Text,
                    TxtWifiBssid.Text.Replace("BSSID : ", ""),
                    TxtWifiFreq.Text,
                    TxtWifiSpeed.Text,
                    TxtWifiMac.Text.Replace("MAC : ", ""),
                    TxtWifiIp.Text.Replace("IP : ", ""),
                    TxtWifiGateway.Text.Replace("Passerelle : ", ""),
                    TxtWifiDns.Text.Replace("DNS : ", ""),
                    stats, duration, pingChartPath, rssiChartPath, TxtWifiLogcat.Text);

                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] 📝 Rapport Word Wi-Fi généré : {outputPath}");
                RecordHistory(new ADBToolsNew.Core.HistoryEntry
                {
                    Type = "Analyse Wi-Fi",
                    Serial = serial,
                    Model = deviceModel ?? "",
                    Client = societe,
                    Incident = incident,
                    Technician = ADBToolsNew.Core.AppSettings.Current.ReportTechnician ?? "",
                    Summary = ADBToolsNew.Core.WifiReportService.BuildSummaryText(stats, duration),
                    Items =
                    {
                        new ADBToolsNew.Core.HistoryItem { Name = "Réseau", Status = TxtWifiSsid.Text },
                        new ADBToolsNew.Core.HistoryItem { Name = "Fréquence", Status = TxtWifiFreq.Text },
                        new ADBToolsNew.Core.HistoryItem { Name = "Débit", Status = TxtWifiSpeed.Text },
                    }
                }, outputPath);
                ShowInExplorer(outputPath);
            }
            catch (Exception ex)
            {
                AppendConsole($"\n[{DateTime.Now:HH:mm:ss}] ❌ Erreur génération du rapport Word : {ex.Message}");
            }
        }

        private async void BtnResetRadar_Click(object sender, RoutedEventArgs e)
        {
            bool confirm = await ShowCustomDialogAsync("🔄 Réinitialiser", "Effacer les statistiques et le graphique de la session Wi-Fi en cours (RSSI, latence, pertes de paquets) ?\n\nCette action est irréversible.", "Réinitialiser", "Annuler", true);
            if (!confirm) return;

            // 1. Sécurité : On arrête le radar s'il est encore en train de tourner
            if (_radarService.IsRunning)
            {
                StopRadar();
            }

            // 2. Remise à zéro des compteurs ET DE LA MÉMOIRE CACHE
            _radarService.Stats.Reset();
            _lastBssid = ""; // On force le programme à oublier l'antenne
            _isFetchingHeavyData = false; // On déverrouille le téléchargement de l'IP
            _isPolling = false; // On débloque le timer

            // 3. Vidage des courbes des Graphiques LiveCharts
            if (PingValues != null) PingValues.Clear();
            if (RssiValues != null) RssiValues.Clear();
            _pingHistory.Clear();
            _rssiHistory.Clear();
            if (PingChart.AxisY.Count > 0) PingChart.AxisY[0].MaxValue = 200;

            // 4. Nettoyage de l'interface (Remise à l'état par défaut)
            TxtWifiSsid.Text = "En attente...";
            TxtWifiBssid.Text = "BSSID : --:--:--:--:--:--";
            TxtWifiIp.Text = "IP : ---.---.---.---";
            TxtWifiMac.Text = "MAC : --:--:--:--:--:--";

            TxtWifiRssi.Text = "- dBm";
            TxtWifiRssi.Foreground = BrushAccent; // Couleur par défaut (Accent)

            TxtWifiSpeed.Text = "- Mbps";
            TxtWifiFreq.Text = "Bande : -- / Canal : --";
            TxtWifiGateway.Text = "Passerelle : ---.---.---.---";
            TxtWifiDns.Text = "DNS : ---.---.---.---";

            TxtPacketLoss.Text = "Perte de paquets : 0%";
            TxtPacketLoss.Foreground = BrushDanger; // Rouge par défaut
            TxtPingCount.Text = "Envoyés : 0";
            TxtPingLatency.Text = "Latence : -- ms";
            TxtPingLatency.Foreground = BrushNeutral; // Gris neutre
            TxtPingMinMaxAvg.Text = "Min/Moy/Max : -- / -- / -- ms";
            TxtRssiMinMaxAvg.Text = "Min/Moy/Max : -- / -- / -- dBm";

            // 5. Vidage de la console Logcat
            TxtWifiLogcat.Text = "--- Prêt pour une nouvelle analyse ---\n";
        }
    }
}
