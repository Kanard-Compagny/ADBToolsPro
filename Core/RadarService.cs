using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Encapsule le moteur de l'Analyse Wi-Fi (Radar) : cycle de vie des process ping/logcat en continu,
    /// sondage périodique de l'état Wi-Fi, résolution MAC/passerelle/DNS, et statistiques de session.
    /// Aucune dépendance UI — MainWindow.Radar.cs se charge d'afficher les résultats.
    /// </summary>
    public class RadarService
    {
        private static readonly Regex BssidRegex = new Regex(@"BSSID:\s*([0-9a-fA-F:]{17})");
        private static readonly Regex SsidNetworkInfoRegex = new Regex(@"type:\s*WIFI\[\].*?extra:\s*""([^""]+)""");
        // Ligne décrivant la connexion COURANTE : "WifiInfo: SSID: ..." (cmd wifi status) ou "mWifiInfo SSID: ..." (dumpsys wifi).
        private static readonly Regex WifiInfoLineRegex = new Regex(@"(?:mWifiInfo\s+|WifiInfo:\s*)(SSID:.*?)(?=\n|\r|$)");
        private static readonly Regex SsidClassicRegex = new Regex(@"SSID:\s*""([^""]+)""");
        private static readonly Regex RssiRegex = new Regex(@"RSSI:\s*(-?\d+)");
        private static readonly Regex SpeedRegex = new Regex(@"Link speed:\s*(\d+\s*Mbps)");
        private static readonly Regex FreqRegex = new Regex(@"(?:freq[=:]|Frequency:)\s*(\d+)");
        private static readonly Regex PingTimeRegex = new Regex(@"time=([0-9.]+)");
        private static readonly Regex PingSeqRegex = new Regex(@"icmp_seq=(\d+)");
        private static readonly Regex MacLinkRegex = new Regex(@"link/ether\s+([0-9a-fA-F:]{17})");
        private static readonly Regex MacDumpRegex = new Regex(@"macAddress=([0-9a-fA-F:]{17})");
        private static readonly Regex GatewayRouteRegex = new Regex(@"default via ([0-9\.]+)");
        private static readonly Regex GatewayConnRegex = new Regex(@"Gateway:\s*/?([0-9\.]+)");
        private static readonly Regex DnsConnRegex = new Regex(@"DnsAddresses:\s*\[([^\]]+)\]");

        private readonly AdbManager _adbManager;
        private readonly AdbStreamProcess _pingProcess = new AdbStreamProcess();
        private readonly AdbStreamProcess _wifiLogcatProcess = new AdbStreamProcess();

        public RadarStats Stats { get; } = new RadarStats();
        public bool IsRunning { get; private set; }

        public RadarService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        private readonly PingSequenceTracker _pingSequence = new PingSequenceTracker();

        // Dernier point d'accès (BSSID) auquel le terminal était connecté : un changement = un roaming.
        private string _lastConnectedBssid;
        private bool _wasConnected;

        // Vrai dès qu'un événement "CTRL-EVENT-DISCONNECTED" de wpa_supplicant a été vu : les déconnexions
        // sont alors comptées depuis le logcat (précis, même pour une micro-coupure de moins d'une seconde).
        // Sinon (tag absent sur certaines versions/OEM), on se rabat sur les transitions observées au sondage.
        private volatile bool _supplicantDisconnectEventsSeen;

        /// <summary>Démarre le ping continu et le logcat Wi-Fi filtré. Réinitialise les statistiques.</summary>
        /// <summary>
        /// Heure actuelle du terminal au format attendu par "logcat -T" ("MM-dd HH:mm:ss.000"), ou null si
        /// indisponible. Permet de ne lire que les événements Wi-Fi postérieurs au lancement de l'analyse.
        /// </summary>
        public async Task<string> GetDeviceLogTimeAsync(string serial)
        {
            try
            {
                var res = await _adbManager.ExecuteAsync($"-s {serial} shell date '+%m-%d %H:%M:%S'", TimeSpan.FromSeconds(5));
                string value = (res.StandardOutput ?? "").Trim();
                return Regex.IsMatch(value, @"^\d{2}-\d{2} \d{2}:\d{2}:\d{2}$") ? value + ".000" : null;
            }
            catch
            {
                return null;
            }
        }

        /// <param name="logcatSince">Heure du terminal (voir <see cref="GetDeviceLogTimeAsync"/>) à partir de laquelle
        /// lire le logcat. Sans elle, logcat rejouait tout l'historique déjà en mémoire au démarrage : d'anciennes
        /// déconnexions étaient comptées dans la nouvelle session, et l'afflux de lignes saturait l'affichage.</param>
        public void Start(string adbExePath, string serial, Action<PingSample> onPingSample, Action<string> onWifiEventLine, string logcatSince = null)
        {
            Stats.Reset();
            Stats.MarkStarted();
            _pingSequence.Reset();
            _lastConnectedBssid = null;
            _wasConnected = false;
            _supplicantDisconnectEventsSeen = false;
            IsRunning = true;

            _pingProcess.Start(adbExePath, $"-s {serial} shell ping 8.8.8.8", rawLine =>
            {
                if (!IsRunning) return;

                var sample = ParsePingLine(rawLine);
                if (!sample.HasValue) return;

                // Le ping Android n'écrit rien pour un paquet perdu : chaque trou dans "icmp_seq" correspond à
                // des pertes réelles, comptées ici AVANT la réponse qui révèle le trou.
                int missing = _pingSequence.RegisterAndCountMissing(sample.Value.Sequence);
                for (int i = 0; i < missing; i++)
                {
                    var lost = new PingSample { Success = false, LatencyMs = 200 };
                    Stats.RecordPing(lost);
                    onPingSample?.Invoke(lost);
                }

                Stats.RecordPing(sample.Value);
                onPingSample?.Invoke(sample.Value);
            });

            // "-T '<heure>'" : uniquement les lignes postérieures à cette heure. À défaut, "-T 1" limite la reprise
            // de l'historique à la dernière ligne.
            string sinceArg = string.IsNullOrEmpty(logcatSince) ? "-T 1" : $"-T '{logcatSince}'";
            _wifiLogcatProcess.Start(adbExePath, $"-s {serial} shell logcat -v time {sinceArg} -s wpa_supplicant WifiStateMachine", line =>
            {
                if (!IsRunning || !IsNotableWifiLogcatLine(line)) return;

                // Le roaming n'est plus compté ici : "COMPLETED" apparaît aussi à la première connexion et à
                // chaque reconnexion, ce qui gonflait le compteur. Il est désormais déduit du changement de
                // BSSID observé au sondage (PollWifiStatusAsync).
                if (line.Contains("CTRL-EVENT-DISCONNECTED"))
                {
                    _supplicantDisconnectEventsSeen = true;
                    Stats.IncrementDisconnect();
                }

                onWifiEventLine?.Invoke(line);
            });
        }

        public void Stop()
        {
            IsRunning = false;
            Stats.MarkStopped();
            _pingProcess.Stop();
            _wifiLogcatProcess.Stop();
        }

        /// <summary>
        /// Sonde l'état Wi-Fi courant et met à jour les stats RSSI. Utilise d'abord "cmd wifi status" (Android 11+) :
        /// quelques lignes décrivant UNIQUEMENT la connexion en cours, renvoyées en une fraction de seconde. "dumpsys
        /// wifi" (plusieurs Mo, plusieurs secondes sur Zebra, et rempli d'historique) ne sert plus que de repli :
        /// c'est lui qui faisait afficher "Déconnecté" pendant près d'une minute après un mode avion.
        /// </summary>
        public async Task<WifiSnapshot> PollWifiStatusAsync(string serial)
        {
            WifiSnapshot snapshot = null;

            var resCmd = await _adbManager.ExecuteAsync($"-s {serial} shell cmd wifi status", TimeSpan.FromSeconds(10));
            string cmdOutput = resCmd.StandardOutput ?? "";

            if (resCmd.IsSuccess && cmdOutput.IndexOf("WifiInfo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                snapshot = ParseWifiDump(cmdOutput);
            }
            else if (resCmd.IsSuccess && IsCmdWifiStatusDisconnected(cmdOutput))
            {
                snapshot = new WifiSnapshot { IsConnected = false, RawDumpsysWifi = cmdOutput };
            }

            if (snapshot == null)
            {
                // Repli (Android 10 et antérieurs, ou sortie inattendue) : ancienne méthode.
                var resDump = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys wifi");
                snapshot = ParseWifiDump(resDump.StandardOutput ?? "");
            }

            if (snapshot.IsConnected && WifiParsingHelpers.NeedsSsidFallback(snapshot.Ssid))
            {
                string fallbackSsid = WifiParsingHelpers.ExtractSsidFromCmdWifiStatus(cmdOutput);
                if (!string.IsNullOrEmpty(fallbackSsid)) snapshot.Ssid = fallbackSsid;
            }

            if (snapshot.IsConnected && snapshot.Rssi.HasValue)
            {
                Stats.RecordRssi(snapshot.Rssi.Value);
            }

            if (IsRunning) TrackConnectionChanges(snapshot);

            return snapshot;
        }

        /// <summary>Compte un roaming à chaque changement de point d'accès, et une déconnexion à chaque passage
        /// connecté → déconnecté si le logcat wpa_supplicant ne fournit pas l'information.</summary>
        private void TrackConnectionChanges(WifiSnapshot snapshot)
        {
            if (snapshot.IsConnected)
            {
                if (!string.IsNullOrEmpty(_lastConnectedBssid)
                    && !string.IsNullOrEmpty(snapshot.Bssid)
                    && !string.Equals(_lastConnectedBssid, snapshot.Bssid, StringComparison.OrdinalIgnoreCase))
                {
                    Stats.IncrementRoaming();
                }

                if (!string.IsNullOrEmpty(snapshot.Bssid)) _lastConnectedBssid = snapshot.Bssid;
            }
            else if (_wasConnected && !_supplicantDisconnectEventsSeen)
            {
                Stats.IncrementDisconnect();
            }

            _wasConnected = snapshot.IsConnected;
        }

        /// <summary>Fonction pure : "cmd wifi status" indique explicitement un Wi-Fi coupé ou non connecté.</summary>
        public static bool IsCmdWifiStatusDisconnected(string cmdOutput)
        {
            string o = (cmdOutput ?? "").ToLowerInvariant();
            return o.Contains("wifi is disabled") || o.Contains("wifi is not connected") || o.Contains("not connected");
        }

        /// <summary>Parse la sortie de "cmd wifi status" ou "dumpsys wifi" en WifiSnapshot. Fonction pure, testable isolément.</summary>
        public static WifiSnapshot ParseWifiDump(string wifiOutput)
        {
            wifiOutput = wifiOutput ?? "";
            var snapshot = new WifiSnapshot { RawDumpsysWifi = wifiOutput };

            // Le BSSID est lu sur la ligne de la connexion courante quand elle existe : la première occurrence
            // de "BSSID:" dans tout "dumpsys wifi" pouvait appartenir à l'historique (ex. une ancienne
            // connexion à 00:00:00:00:00:00 juste après un mode avion), d'où un "Déconnecté" à tort.
            var mCurrentInfo = WifiInfoLineRegex.Match(wifiOutput);
            var mBssid = BssidRegex.Match(mCurrentInfo.Success ? mCurrentInfo.Groups[1].Value : wifiOutput);
            snapshot.IsConnected = mBssid.Success
                && mBssid.Groups[1].Value != "00:00:00:00:00:00"
                && mBssid.Groups[1].Value != "02:00:00:00:00:00";

            if (!snapshot.IsConnected) return snapshot;

            snapshot.Bssid = mBssid.Groups[1].Value;

            // --- CONTOURNEMENT ZEBRA 101 ---
            var mSsidNetworkInfo = SsidNetworkInfoRegex.Match(wifiOutput);
            var mWifiInfoLine = WifiInfoLineRegex.Match(wifiOutput);
            string infoLine = mWifiInfoLine.Success ? mWifiInfoLine.Groups[1].Value : wifiOutput;
            var mSsidClassic = SsidClassicRegex.Match(infoLine);

            snapshot.Ssid = "Inconnu";
            if (mSsidNetworkInfo.Success && mSsidNetworkInfo.Groups[1].Value != "<unknown ssid>")
                snapshot.Ssid = mSsidNetworkInfo.Groups[1].Value;
            else if (mSsidClassic.Success && mSsidClassic.Groups[1].Value != "<unknown ssid>")
                snapshot.Ssid = mSsidClassic.Groups[1].Value;

            var mRssi = RssiRegex.Match(infoLine);
            if (mRssi.Success && int.TryParse(mRssi.Groups[1].Value, out int rssiVal))
                snapshot.Rssi = rssiVal;

            var mSpeed = SpeedRegex.Match(infoLine);
            snapshot.LinkSpeed = mSpeed.Success ? mSpeed.Groups[1].Value : "- Mbps";

            var mFreq = FreqRegex.Match(infoLine);
            if (mFreq.Success && int.TryParse(mFreq.Groups[1].Value, out int freq))
            {
                // Le Wi-Fi 6E (5925-7125 MHz) utilise une bande et une numérotation de canaux distinctes du 5 GHz classique.
                if (freq >= 5925)
                {
                    snapshot.Band = "6 GHz";
                    snapshot.Channel = (freq - 5950) / 5;
                }
                else if (freq >= 5000)
                {
                    snapshot.Band = "5 GHz";
                    snapshot.Channel = (freq - 5000) / 5;
                }
                else
                {
                    snapshot.Band = "2.4 GHz";
                    // Le canal 14 (2484 MHz, Japon) ne suit pas la règle des 5 MHz.
                    snapshot.Channel = freq == 2484 ? 14 : (freq - 2407) / 5;
                }
            }

            return snapshot;
        }

        /// <summary>Classe une ligne brute de "ping" en succès/échec. Retourne null si la ligne n'est pas pertinente.</summary>
        public static PingSample? ParsePingLine(string rawLine)
        {
            string line = (rawLine ?? "").ToLowerInvariant();

            var mSeq = PingSeqRegex.Match(line);
            int? seq = mSeq.Success && int.TryParse(mSeq.Groups[1].Value, out int s) ? s : (int?)null;

            if (line.Contains("time="))
            {
                var matchTime = PingTimeRegex.Match(line);
                if (matchTime.Success && double.TryParse(matchTime.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double ms))
                {
                    return new PingSample { Success = true, LatencyMs = (int)ms, Sequence = seq };
                }
                return null;
            }

            // "packet loss" n'apparaît que dans le résumé final (jamais affiché ici : le process est tué à
            // l'arrêt) — il ne doit pas compter comme un paquet perdu supplémentaire.
            if (line.Contains("unreachable") || line.Contains("timeout") || line.Contains("no answer"))
            {
                return new PingSample { Success = false, LatencyMs = 200, Sequence = seq };
            }

            return null;
        }

        /// <summary>Indique si une ligne du logcat Wi-Fi filtré mérite d'être comptée/affichée.</summary>
        public static bool IsNotableWifiLogcatLine(string line)
        {
            return line.Contains("CTRL-EVENT-") || line.Contains("state=") || line.Contains("roaming");
        }

        public async Task<string> GetDeviceMacAddressAsync(string serial, string wifiDump)
        {
            // 1. MÉTHODE DU BANC DE TEST (La plus fiable)
            var resIp = await _adbManager.ExecuteAsync($"-s {serial} shell ip addr show wlan0");
            var mLink = MacLinkRegex.Match(resIp.StandardOutput ?? "");
            if (mLink.Success) return mLink.Groups[1].Value;

            // 2. FALLBACK ZEBRA (Si la carte Wi-Fi est éteinte)
            var resPropZebra = await _adbManager.ExecuteAsync($"-s {serial} shell getprop ro.mac.wlan");
            string zebraMac = resPropZebra.StandardOutput?.Trim();
            if (!string.IsNullOrEmpty(zebraMac) && zebraMac.Length == 17) return zebraMac;

            // 3. FALLBACK DUMPSYS
            var mMac = MacDumpRegex.Match(wifiDump ?? "");
            if (mMac.Success && mMac.Groups[1].Value != "02:00:00:00:00:00") return mMac.Groups[1].Value;

            return "Masquée par Android";
        }

        public Task<string> GetDeviceGatewayAsync(string serial) => ResolveGatewayAsync(_adbManager, serial);

        public Task<string> GetDeviceDnsAsync(string serial) => ResolveDnsAsync(_adbManager, serial);

        /// <summary>Passerelle par défaut (ip route, puis dumpsys connectivity). Statique pour être partagée avec
        /// le check-up SAV (DiagnosticService), qui lisait "dhcp.wlan0.gateway" — propriété supprimée depuis
        /// Android 8. Retourne "---.---.---.---" si introuvable.</summary>
        public static async Task<string> ResolveGatewayAsync(AdbManager adbManager, string serial)
        {
            var resRoute = await adbManager.ExecuteAsync($"-s {serial} shell ip route show table all");
            var mGw = GatewayRouteRegex.Match(resRoute.StandardOutput ?? "");
            if (mGw.Success) return mGw.Groups[1].Value;

            var resConn = await adbManager.ExecuteAsync($"-s {serial} shell dumpsys connectivity");
            var mGwConn = GatewayConnRegex.Match(resConn.StandardOutput ?? "");
            if (mGwConn.Success) return mGwConn.Groups[1].Value;

            return "---.---.---.---";
        }

        /// <summary>Serveur DNS principal (dumpsys connectivity, puis net.dns1 pour Android &lt; 8). Partagé avec
        /// le check-up SAV. Retourne "---.---.---.---" si introuvable.</summary>
        public static async Task<string> ResolveDnsAsync(AdbManager adbManager, string serial)
        {
            // 1. Méthode Android 10+ (Cherche le tableau "DnsAddresses: [/8.8.8.8]")
            var resConn = await adbManager.ExecuteAsync($"-s {serial} shell dumpsys connectivity");
            var mDnsConn = DnsConnRegex.Match(resConn.StandardOutput ?? "");
            if (mDnsConn.Success)
            {
                string[] dnsList = mDnsConn.Groups[1].Value.Split(',');
                if (dnsList.Length > 0 && dnsList[0].Contains("."))
                    return dnsList[0].Replace("/", "").Trim();
            }

            // 2. Ancienne méthode Android < 10
            var resProp = await adbManager.ExecuteAsync($"-s {serial} shell getprop net.dns1");
            string propDns = resProp.StandardOutput?.Trim();
            if (!string.IsNullOrEmpty(propDns) && propDns.Contains("."))
                return propDns.Replace("/", "");

            return "---.---.---.---";
        }
    }
}
