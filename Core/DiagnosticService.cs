using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>Résultat d'un test individuel du check-up automatique.</summary>
    public class DiagTestResult
    {
        public string StatusIcon;
        public string Details;
    }

    /// <summary>
    /// Libellés des codes "dumpsys battery" (BatteryManager.BATTERY_STATUS_* / BATTERY_HEALTH_*), partagés entre
    /// le Dashboard et le check-up SAV. Auparavant deux tables différentes coexistaient, et le Dashboard
    /// affichait "Normale" pour Inconnue (1), Défaillance (6) et Trop froide (7).
    /// </summary>
    public static class BatteryLabels
    {
        public static string Status(int code)
        {
            switch (code)
            {
                case 2: return "En charge ⚡";
                case 3: return "Sur batterie 🔋";
                case 4: return "Ne charge pas ⚠️";
                case 5: return "Pleine 💯";
                default: return "Inconnu";
            }
        }

        public static string Health(int code)
        {
            switch (code)
            {
                case 2: return "Bonne (Good) ✅";
                case 3: return "Surchauffe (Overheat) 🔴";
                case 4: return "Hors service (Dead) ❌";
                case 5: return "Surtension ⚡";
                case 6: return "Défaillance (Failure) ❌";
                case 7: return "Trop froide ❄️";
                default: return "Inconnue";
            }
        }

        public static bool IsHealthy(int code) => code == 2;
    }

    /// <summary>
    /// Encapsule le moteur du check-up automatique du Banc de Test SAV : un test par système
    /// (authentification, root, Wi-Fi, ping, batterie, stockage), plus la génération du rapport RMA.
    /// Aucune dépendance UI.
    /// </summary>
    public class DiagnosticService
    {
        private readonly AdbManager _adbManager;

        public DiagnosticService(AdbManager adbManager)
        {
            _adbManager = adbManager;
        }

        /// <summary>Vérifie que l'appareil est toujours présent et autorisé avant de lancer un test.</summary>
        public async Task<bool> IsDeviceStillPresentAsync(string serial)
        {
            var stateCheck = await _adbManager.ExecuteAsync($"-s {serial} get-state");
            return stateCheck.IsSuccess && stateCheck.StandardOutput.Trim().Equals("device", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Exécute le test correspondant au SystemId donné (auth/root/wifi/ping/battery/storage).</summary>
        public Task<DiagTestResult> RunTestAsync(string systemId, string serial)
        {
            switch (systemId)
            {
                case "auth": return CheckAuthAsync(serial);
                case "root": return CheckRootAsync(serial);
                case "wifi": return CheckWifiAsync(serial);
                case "ping": return CheckPingAsync(serial);
                case "battery": return CheckBatteryAsync(serial);
                case "storage": return CheckStorageAsync(serial);
                case "sensors": return CheckSensorsAsync(serial);
                case "thermal": return CheckThermalAsync(serial);
                case "crashes": return CheckCrashHistoryAsync(serial);
                default: return Task.FromResult<DiagTestResult>(null);
            }
        }

        /// <summary>
        /// Libellé "Marque Modèle" commun aux rapports SAV et Wi-Fi (ex. "zebra" + "TC53" → "Zebra TC53").
        /// La marque n'est pas répétée si le modèle la contient déjà ("Samsung" + "Samsung SM-A515F").
        /// </summary>
        public static string FormatBrandModel(string brand, string model)
        {
            brand = (brand ?? "").Trim();
            model = (model ?? "").Trim();
            if (brand.Length > 0) brand = char.ToUpperInvariant(brand[0]) + brand.Substring(1);

            if (brand.Length == 0) return model;
            if (model.Length == 0) return brand;
            if (model.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) return model;
            return $"{brand} {model}";
        }

        private async Task<DiagTestResult> CheckAuthAsync(string serial)
        {
            // 1. INFOS SYSTÈME
            // Un seul "getprop" (auparavant 4 + 5 appels adb pour l'identité et l'IMEI).
            var props = await _adbManager.GetAllPropsAsync(serial);
            string Prop(string key) => props.TryGetValue(key, out var v) ? v.Trim() : "";

            string imei = await ResolveImeiAsync(serial, props);

            string model = Prop("ro.product.model");
            string brand = Prop("ro.product.brand");
            string androidVer = Prop("ro.build.version.release");
            string build = Prop("ro.build.display.id");

            if (string.IsNullOrEmpty(model) || model.Contains("error"))
            {
                return new DiagTestResult
                {
                    Details = $"• Numéro de Série : {serial}\n• Statut : 🔒 En attente d'autorisation\n👉 Veuillez cocher 'Toujours autoriser cet ordinateur' sur l'écran du terminal.",
                    StatusIcon = "⚠️"
                };
            }

            StringBuilder sbAuth = new StringBuilder();
            sbAuth.AppendLine("--- IDENTITÉ DU TERMINAL ---");
            sbAuth.AppendLine($"• Numéro de Série : {serial}");
            sbAuth.AppendLine($"• IMEI : {imei ?? "Non disponible (restreint par Android/OEM sur ce terminal)"}");
            sbAuth.AppendLine($"• Modèle : {FormatBrandModel(brand, model)}");
            sbAuth.AppendLine($"• Version Android : Android {androidVer}");
            sbAuth.AppendLine($"• Build OS : {build}");

            return new DiagTestResult { Details = sbAuth.ToString(), StatusIcon = "✅" };
        }

        // Propriétés système où l'IMEI peut se trouver selon la marque/version (ordre = probabilité décroissante).
        private static readonly string[] ImeiPropKeys =
        {
            "persist.radio.imei", "persist.radio.imei1", "ril.gsm.imei", "persist.sys.imei", "ro.ril.oem.imei"
        };

        /// <summary>
        /// Tente plusieurs méthodes de récupération de l'IMEI, du plus courant au plus profond, car Android 10+
        /// restreint fortement cet identifiant (READ_PRIVILEGED_PHONE_STATE) et son exposition varie selon l'OEM.
        /// Retourne null si aucune méthode n'aboutit (restriction plateforme, pas un bug de l'app).
        /// </summary>
        private async Task<string> ResolveImeiAsync(string serial, System.Collections.Generic.Dictionary<string, string> props)
        {
            // 1. Propriétés système classiques (répandues sur le parc entreprise/rugged), déjà lues en un seul getprop.
            foreach (var key in ImeiPropKeys)
            {
                string val = props != null && props.TryGetValue(key, out var v) ? v.Trim() : null;
                if (LooksLikeImei(val)) return val;
            }

            // 2. dumpsys iphonesubinfo (fonctionne encore sur une partie du parc, notamment antérieur à Android 10).
            var resDump = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys iphonesubinfo");
            var mImei = Regex.Match(resDump.StandardOutput ?? "", @"Device ID\s*=\s*(\d+)");
            if (mImei.Success) return mImei.Groups[1].Value;

            // 3. Appel direct du service AIDL : contourne souvent la restriction qui bloque "dumpsys" seul.
            var resService = await _adbManager.ExecuteAsync($"-s {serial} shell service call iphonesubinfo 1");
            string fromService = ParseServiceCallImei(resService.StandardOutput);
            if (!string.IsNullOrEmpty(fromService)) return fromService;

            return null;
        }

        private static bool LooksLikeImei(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Count(char.IsDigit) >= 10;

        /// <summary>
        /// Décode la sortie brute de "service call iphonesubinfo 1" (Parcel Android) pour en extraire l'IMEI.
        /// Fonction pure, testable. Retourne null si le résultat ne ressemble pas à un IMEI plausible.
        /// </summary>
        public static string ParseServiceCallImei(string serviceCallOutput)
        {
            if (string.IsNullOrEmpty(serviceCallOutput)) return null;

            // Chaque ligne du dump Parcel se termine par la représentation ASCII entre guillemets simples
            // (ex: '........1.5.2.5.') — les caractères non imprimables (dont le padding UTF-16) sont des points.
            var quoted = Regex.Matches(serviceCallOutput, "'([^']*)'");
            var sb = new StringBuilder();
            foreach (Match m in quoted) sb.Append(m.Groups[1].Value);

            string digitsOnly = Regex.Replace(sb.ToString(), @"[^0-9]", "");

            // Un IMEI fait 14 à 16 chiffres (15 en standard, parfois IMEISV 16, ou tronqué à 14).
            return (digitsOnly.Length >= 14 && digitsOnly.Length <= 16) ? digitsOnly : null;
        }

        private async Task<DiagTestResult> CheckRootAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell su -c id");
            bool isRoot = res.StandardOutput.Contains("uid=0(root)");
            return new DiagTestResult
            {
                Details = isRoot
                    ? "Le terminal dispose d'un accès Root natif (Mode SuperUtilisateur activé)."
                    : "Le terminal est verrouillé (Non-Rooté), comportement standard sécurisé.",
                StatusIcon = isRoot ? "✅" : "➖"
            };
        }

        private async Task<DiagTestResult> CheckWifiAsync(string serial)
        {
            var resWifiDump = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys wifi");
            var resIp = await _adbManager.ExecuteAsync($"-s {serial} shell ip addr show wlan0");
            // "dhcp.wlan0.gateway" et "net.dns1" n'existent plus depuis Android 8 : mêmes méthodes que le Radar.
            string gateway = CleanUnresolvedAddress(await RadarService.ResolveGatewayAsync(_adbManager, serial));
            string dns = CleanUnresolvedAddress(await RadarService.ResolveDnsAsync(_adbManager, serial));

            string wifiOutput = resWifiDump.StandardOutput ?? "";

            // Repli robuste si "dumpsys wifi" ne remonte pas un SSID exploitable (notamment certains
            // terminaux Zebra, où le SSID apparaît comme "101" ou reste vide alors que le Wi-Fi est actif).
            var mSsidPreview = Regex.Match(wifiOutput, @"(?<!B)SSID:\s*""?([^"",\r\n]+)""?");
            string previewSsid = mSsidPreview.Success ? mSsidPreview.Groups[1].Value.Trim() : "Inconnu";

            string ssidOverride = null;
            if (WifiParsingHelpers.NeedsSsidFallback(previewSsid))
            {
                var resCmd = await _adbManager.ExecuteAsync($"-s {serial} shell cmd wifi status");
                ssidOverride = WifiParsingHelpers.ExtractSsidFromCmdWifiStatus(resCmd.StandardOutput);
            }

            return ParseWifiCheck(wifiOutput, resIp.StandardOutput, gateway, dns, ssidOverride);
        }

        /// <summary>Les résolveurs du Radar renvoient "---.---.---.---" quand rien n'est trouvé : vide ici.</summary>
        private static string CleanUnresolvedAddress(string value) =>
            string.IsNullOrWhiteSpace(value) || value.StartsWith("---") ? "" : value.Trim();

        /// <summary>Fonction pure, testable : construit le résultat du test Wi-Fi à partir des sorties brutes.
        /// <paramref name="ssidOverride"/> permet d'imposer un SSID déjà résolu via le repli "cmd wifi status".</summary>
        public static DiagTestResult ParseWifiCheck(string wifiOutput, string ipOutput, string gateway, string dns, string ssidOverride = null)
        {
            string ssid = "Inconnu", bssid = "Inconnu", rssi = "N/A", linkSpeed = "N/A", freq = "N/A", ipAddr = "N/A", macAddr = "N/A";

            // (?<!B) : ne pas confondre avec "BSSID:", qui contient lui aussi "SSID:".
            var mSsid = Regex.Match(wifiOutput ?? "", @"(?<!B)SSID:\s*""?([^"",\r\n]+)""?");
            if (mSsid.Success) ssid = mSsid.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(ssidOverride)) ssid = ssidOverride;

            var mBssid = Regex.Match(wifiOutput ?? "", @"BSSID:\s*([0-9a-fA-F:]{17})");
            if (mBssid.Success) bssid = mBssid.Groups[1].Value;

            var mRssi = Regex.Match(wifiOutput ?? "", @"RSSI:\s*(-?\d+)");
            if (mRssi.Success) rssi = $"{mRssi.Groups[1].Value} dBm";

            var mSpeed = Regex.Match(wifiOutput ?? "", @"Link speed:\s*(\d+\s*Mbps)");
            if (mSpeed.Success) linkSpeed = mSpeed.Groups[1].Value;

            var mFreq = Regex.Match(wifiOutput ?? "", @"Frequency:\s*(\d+\s*MHz)");
            if (mFreq.Success) freq = mFreq.Groups[1].Value;

            var mIp = Regex.Match(ipOutput ?? "", @"inet\s+([0-9\.]+)/\d+");
            if (mIp.Success) ipAddr = mIp.Groups[1].Value;

            var mMac = Regex.Match(ipOutput ?? "", @"link/ether\s+([0-9a-fA-F:]{17})");
            if (mMac.Success) macAddr = mMac.Groups[1].Value;

            bool isConnected = (ssid != "Inconnu" && ssid != "<unknown ssid>" && !string.IsNullOrEmpty(ipAddr) && ipAddr != "N/A");

            StringBuilder sbWifi = new StringBuilder();
            sbWifi.AppendLine($"• Statut : {(isConnected ? "Connecté ✅" : "Déconnecté ❌")}");
            sbWifi.AppendLine($"• Réseau (SSID) : {ssid}");
            sbWifi.AppendLine($"• Point d'accès (BSSID) : {bssid}");
            sbWifi.AppendLine($"• Adresse IP Locale : {ipAddr}");
            sbWifi.AppendLine($"• Adresse MAC Terminal : {macAddr}");
            sbWifi.AppendLine($"• Puissance Signal (RSSI) : {rssi}");
            sbWifi.AppendLine($"• Vitesse de Liaison : {linkSpeed}");
            sbWifi.AppendLine($"• Fréquence : {freq}");
            if (!string.IsNullOrEmpty(gateway)) sbWifi.AppendLine($"• Passerelle (Gateway) : {gateway}");
            if (!string.IsNullOrEmpty(dns)) sbWifi.AppendLine($"• Serveur DNS : {dns}");

            return new DiagTestResult { Details = sbWifi.ToString(), StatusIcon = isConnected ? "✅" : "❌" };
        }

        private async Task<DiagTestResult> CheckPingAsync(string serial)
        {
            var resPing1 = await _adbManager.ExecuteAsync($"-s {serial} shell ping -c 1 -W 2 1.1.1.1");
            var resPing2 = await _adbManager.ExecuteAsync($"-s {serial} shell ping -c 1 -W 2 8.8.8.8");

            bool ping1Ok = IsPingSuccess(resPing1.StandardOutput);
            bool ping2Ok = IsPingSuccess(resPing2.StandardOutput);
            bool internetOk = ping1Ok || ping2Ok;

            StringBuilder sbPing = new StringBuilder();
            sbPing.AppendLine("--- DIAGNOSTIC CONNECTIVITÉ ---");
            sbPing.AppendLine($"• Ping Cloudflare (1.1.1.1) : {(ping1Ok ? "Succès ✅" : "Échec ❌")}");
            sbPing.AppendLine($"• Ping Google (8.8.8.8) : {(ping2Ok ? "Succès ✅" : "Échec ❌")}");

            string gwIp = CleanUnresolvedAddress(await RadarService.ResolveGatewayAsync(_adbManager, serial));
            if (!string.IsNullOrEmpty(gwIp))
            {
                var resPingGw = await _adbManager.ExecuteAsync($"-s {serial} shell ping -c 1 -W 2 {gwIp}");
                bool gwOk = IsPingSuccess(resPingGw.StandardOutput);
                sbPing.AppendLine($"• Ping Routeur Local ({gwIp}) : {(gwOk ? "Succès ✅" : "Échec ❌")}");
            }

            return new DiagTestResult { Details = sbPing.ToString(), StatusIcon = internetOk ? "✅" : "❌" };
        }

        private static bool IsPingSuccess(string output)
        {
            output = output ?? "";
            return output.Contains("bytes from") || output.Contains("ttl=") || output.Contains("1 received");
        }

        // "dumpsys battery" n'expose quasiment jamais le nombre de cycles en standard AOSP : c'est une extension
        // OEM rare. La vraie source, bien plus universelle, est le nœud noyau power_supply — mais son nom varie
        // trop selon la plateforme pour être deviné à l'avance (testé : "battery"/"bms"/"pmic-battery" ne
        // correspondaient à aucun des deux Zebra du parc). On énumère donc les nœuds RÉELLEMENT présents sur
        // l'appareil et on teste "cycle_count" sur chacun, plutôt que de parier sur un nom fixe.
        private async Task<string> ResolveCycleCountAsync(string serial)
        {
            // Un seul appel pour tous les nœuds (auparavant un "cat" par nœud, soit 10 à 20 appels adb).
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell {AdbManager.QuoteLocalArg("cat /sys/class/power_supply/*/cycle_count 2>/dev/null")}");
            foreach (var line in SplitNonEmptyLines(res.StandardOutput))
            {
                if (int.TryParse(line.Trim(), out int cycles) && cycles >= 0)
                {
                    return cycles.ToString();
                }
            }
            return null;
        }

        private async Task<DiagTestResult> CheckBatteryAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys battery");
            string cycleCountOverride = await ResolveCycleCountAsync(serial);
            return ParseBatteryCheck(res.StandardOutput, cycleCountOverride);
        }

        /// <summary>Fonction pure, testable : construit le résultat du test batterie à partir de "dumpsys battery".
        /// <paramref name="cycleCountOverride"/> permet d'imposer un nombre de cycles déjà résolu via sysfs (plus fiable).</summary>
        public static DiagTestResult ParseBatteryCheck(string batRaw, string cycleCountOverride = null)
        {
            batRaw = batRaw ?? "";
            var mLevel = Regex.Match(batRaw, @"level:\s*(\d+)");
            var mHealth = Regex.Match(batRaw, @"health:\s*(\d+)");
            var mStatus = Regex.Match(batRaw, @"status:\s*(\d+)");
            var mTemp = Regex.Match(batRaw, @"temperature:\s*(\d+)");
            var mVolt = Regex.Match(batRaw, @"voltage:\s*(\d+)");

            // Regex avancées pour les Smart Batteries (Zebra/Honeywell)
            var mSerial = Regex.Match(batRaw, @"(?i)(?:serial[_\s]?number|serial):\s*([a-zA-Z0-9]+)");
            var mMfg = Regex.Match(batRaw, @"(?i)(?:mfg[_\s]?date|manufacture[_\s]?date):\s*([0-9a-zA-Z-]+)");
            var mCycle = Regex.Match(batRaw, @"(?i)(?:cycle[_\s]?count|cycles):\s*(\d+)");

            if (!mLevel.Success)
            {
                return new DiagTestResult { Details = "Impossible de lire l'état de la batterie sur ce terminal.", StatusIcon = "❌" };
            }

            string level = mLevel.Groups[1].Value;

            string status = BatteryLabels.Status(mStatus.Success && int.TryParse(mStatus.Groups[1].Value, out int st) ? st : 1);
            string health = BatteryLabels.Health(mHealth.Success && int.TryParse(mHealth.Groups[1].Value, out int he) ? he : 1);

            string temp = "N/A";
            if (mTemp.Success && int.TryParse(mTemp.Groups[1].Value, out int t)) temp = (t / 10.0).ToString("0.0") + " °C";

            string volt = "N/A";
            if (mVolt.Success && int.TryParse(mVolt.Groups[1].Value, out int v)) volt = (v / 1000.0).ToString("0.00") + " V";

            string batSerial = mSerial.Success ? mSerial.Groups[1].Value : "Non disponible";
            string batMfg = mMfg.Success ? mMfg.Groups[1].Value : "Non disponible";
            string batCycle = !string.IsNullOrEmpty(cycleCountOverride)
                ? cycleCountOverride
                : (mCycle.Success ? mCycle.Groups[1].Value : "Non disponible (non exposé par ce SoC/OEM)");

            StringBuilder sbBat = new StringBuilder();
            sbBat.AppendLine("--- BILAN DE SANTÉ BATTERIE ---");
            sbBat.AppendLine($"• Niveau actuel : {level} %");
            sbBat.AppendLine($"• État : {status}");
            sbBat.AppendLine($"• Santé matérielle : {health}");
            sbBat.AppendLine($"• Température : {temp}");
            sbBat.AppendLine($"• Tension : {volt}");
            sbBat.AppendLine("--- DONNÉES SMART BATTERY ---");
            sbBat.AppendLine($"• Cycles de charge : {batCycle}");
            sbBat.AppendLine($"• N° de Série Batterie : {batSerial}");
            sbBat.AppendLine($"• Date de Fabrication : {batMfg}");

            return new DiagTestResult
            {
                Details = sbBat.ToString(),
                StatusIcon = BatteryLabels.IsHealthy(mHealth.Success && int.TryParse(mHealth.Groups[1].Value, out int h2) ? h2 : 1) ? "✅" : "⚠️"
            };
        }

        private async Task<DiagTestResult> CheckStorageAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell df -h /data");
            return ParseStorageCheck(res.StandardOutput);
        }

        private static readonly Regex StorageValuesRegex = new Regex(@"([0-9.,]+[GMK])\s+([0-9.,]+[GMK])\s+([0-9.,]+[GMK])\s+([0-9]+%)");

        /// <summary>
        /// Extrait (Total, Utilisé, Libre, %Utilisé) d'une sortie "df -h /data" : ex "53G 21G 32G 40%".
        /// Partagé entre le test SAV (ParseStorageCheck) et la jauge de stockage du Dashboard.
        /// </summary>
        public static (string Total, string Used, string Free, int UsedPercent)? ParseStorageValues(string storageRaw)
        {
            var m = StorageValuesRegex.Match(storageRaw ?? "");
            if (!m.Success) return null;
            return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, int.Parse(m.Groups[4].Value.Replace("%", "")));
        }

        /// <summary>Fonction pure, testable : construit le résultat du test stockage à partir de "df -h /data".</summary>
        public static DiagTestResult ParseStorageCheck(string storageRaw)
        {
            storageRaw = storageRaw ?? "";

            var values = ParseStorageValues(storageRaw);
            if (values == null)
            {
                // Fallback propre si Regex rate
                return new DiagTestResult
                {
                    Details = $"Réponse brute (Format non standard) :\n{storageRaw.Trim()}",
                    StatusIcon = storageRaw.Contains("/data") ? "✅" : "❌"
                };
            }

            StringBuilder sbStorage = new StringBuilder();
            sbStorage.AppendLine("--- MÉMOIRE INTERNE (/data) ---");
            sbStorage.AppendLine($"• Espace Total : {values.Value.Total}");
            sbStorage.AppendLine($"• Espace Utilisé : {values.Value.Used} ({values.Value.UsedPercent}%)");
            sbStorage.AppendLine($"• Espace Libre : {values.Value.Free}");

            return new DiagTestResult { Details = sbStorage.ToString(), StatusIcon = values.Value.UsedPercent >= 95 ? "⚠️" : "✅" };
        }

        // Types de capteurs standard AOSP (chaînes stables définies par la plateforme, contrairement au format
        // texte libre de "dumpsys sensorservice" qui varie selon les OEM) : Libellé affiché, type Android, essentiel ?
        private static readonly (string Label, string TypeString, bool Essential)[] CoreSensors =
        {
            ("Accéléromètre", "android.sensor.accelerometer", true),
            ("Proximité", "android.sensor.proximity", true),
            ("Luminosité", "android.sensor.light", true),
            ("Gyroscope", "android.sensor.gyroscope", false),
            ("Champ magnétique (Boussole)", "android.sensor.magnetic_field", false),
            ("Baromètre", "android.sensor.pressure", false),
        };

        private async Task<DiagTestResult> CheckSensorsAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys sensorservice");
            return ParseSensorCheck(res.StandardOutput);
        }

        /// <summary>
        /// Fonction pure, testable : vérifie la présence/reconnaissance matérielle des capteurs courants.
        /// Ne mesure PAS de valeurs en temps réel (nécessiterait une app compagnon avec un listener actif) —
        /// un capteur "Absent" alors qu'il devrait exister sur ce modèle est un signal fort de panne matérielle.
        /// </summary>
        public static DiagTestResult ParseSensorCheck(string dumpOutput)
        {
            dumpOutput = dumpOutput ?? "";
            bool essentialMissing = false;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- CAPTEURS DÉTECTÉS PAR LE SYSTÈME ---");
            foreach (var sensor in CoreSensors)
            {
                bool present = dumpOutput.IndexOf(sensor.TypeString, StringComparison.OrdinalIgnoreCase) >= 0;
                sb.AppendLine($"• {sensor.Label} : {(present ? "Présent ✅" : "Absent ⚠️")}");
                if (!present && sensor.Essential) essentialMissing = true;
            }
            sb.AppendLine();
            sb.AppendLine("ℹ️ Ce test confirme la reconnaissance matérielle par l'OS, pas une lecture en direct des valeurs.");
            sb.AppendLine("   Un capteur absent n'est pas forcément anormal : vérifiez s'il est prévu sur ce modèle.");

            return new DiagTestResult { Details = sb.ToString(), StatusIcon = essentialMissing ? "⚠️" : "✅" };
        }

        private async Task<DiagTestResult> CheckThermalAsync(string serial)
        {
            // Une ligne "type|temp" par zone, lue dans la MÊME boucle : avec deux "cat" séparés, une zone dont
            // la température est illisible (fréquent sur Qualcomm) décalait toutes les suivantes, et chaque
            // température était attribuée à la mauvaise zone.
            const string script = "for z in /sys/class/thermal/thermal_zone*; do echo \"$(cat $z/type 2>/dev/null)|$(cat $z/temp 2>/dev/null)\"; done";
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell {AdbManager.QuoteLocalArg(script)}");
            var (types, temps) = SplitThermalZoneLines(res.StandardOutput);
            return ParseThermalCheck(string.Join("\n", types), string.Join("\n", temps));
        }

        /// <summary>Fonction pure, testable : découpe les lignes "type|temp" en deux listes alignées, en ignorant
        /// les zones sans température lisible.</summary>
        public static (List<string> Types, List<string> Temps) SplitThermalZoneLines(string output)
        {
            var types = new List<string>();
            var temps = new List<string>();
            foreach (var line in SplitNonEmptyLines(output))
            {
                int sep = line.LastIndexOf('|');
                if (sep <= 0) continue;
                string type = line.Substring(0, sep).Trim();
                string temp = line.Substring(sep + 1).Trim();
                if (type.Length == 0 || temp.Length == 0) continue;
                types.Add(type);
                temps.Add(temp);
            }
            return (types, temps);
        }

        // Plage de plausibilité pour une vraie température d'appareil électronique. De nombreuses zones
        // "thermal_zone" exposées par les SoC Qualcomm ne sont PAS des températures malgré leur nom (ex: "soc"
        // = état de charge batterie en %, "ibat-high"/"ibat-vhigh" = seuils de courant en mA) : sans ce filtre,
        // elles polluent le calcul avec des valeurs absurdes (100°C, 160°C, -274°C...).
        private const double PlausibleMinCelsius = -20.0;
        private const double PlausibleMaxCelsius = 100.0;

        // Zones "thermal_zone" connues pour exposer autre chose qu'une température malgré leur nom
        // (ex: "soc" = état de charge batterie en %, observé à "100.0°C" sur un Zebra MC3300x à 100% de charge).
        // Un simple filtre de plage ne suffit pas : 100% de charge tombe pile dans la plage plausible (-20 à 100°C).
        private static readonly string[] KnownNonThermalZoneNames = { "soc" };

        /// <summary>Fonction pure, testable : construit le bilan thermique à partir des zones du noyau Linux (interface standard, indépendante de l'OEM).</summary>
        public static DiagTestResult ParseThermalCheck(string typesOutput, string tempsOutput)
        {
            var types = SplitNonEmptyLines(typesOutput);
            var temps = SplitNonEmptyLines(tempsOutput);

            if (types.Count == 0 || temps.Count == 0)
            {
                return new DiagTestResult
                {
                    Details = "Zones thermiques non exposées par ce terminal (non supporté par ce SoC/cette surcouche OEM).",
                    StatusIcon = "➖"
                };
            }

            int count = Math.Min(types.Count, temps.Count);
            var readings = new List<(string Zone, double Celsius)>();

            for (int i = 0; i < count; i++)
            {
                if (KnownNonThermalZoneNames.Contains(types[i].Trim(), StringComparer.OrdinalIgnoreCase)) continue;

                if (double.TryParse(temps[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double raw))
                {
                    // La plupart des noyaux exposent la température en millidegrés (ex: 35000 = 35.0°C) ;
                    // certains SoC/OEM exposent directement des degrés entiers (valeurs < 1000).
                    double celsius = Math.Abs(raw) > 1000 ? raw / 1000.0 : raw;

                    if (celsius >= PlausibleMinCelsius && celsius <= PlausibleMaxCelsius)
                    {
                        readings.Add((types[i], celsius));
                    }
                }
            }

            if (readings.Count == 0)
            {
                return new DiagTestResult
                {
                    Details = $"{count} zone(s) thermique(s) exposée(s) par le noyau, mais aucune ne contient une valeur de température exploitable (probablement des seuils/limites, pas des mesures).",
                    StatusIcon = "➖"
                };
            }

            var sorted = readings.OrderByDescending(r => r.Celsius).ToList();
            double maxTempC = sorted[0].Celsius;
            double avgTempC = readings.Average(r => r.Celsius);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- BILAN THERMIQUE ---");
            sb.AppendLine($"• Température maximale : {maxTempC:F1} °C (zone : {sorted[0].Zone})");
            sb.AppendLine($"• Température moyenne : {avgTempC:F1} °C");
            sb.AppendLine($"• Zones exploitables : {readings.Count} / {count} exposées par le noyau");
            sb.AppendLine();
            sb.AppendLine("Zones les plus chaudes :");
            foreach (var r in sorted.Take(5))
            {
                sb.AppendLine($"  • {r.Zone} : {r.Celsius:F1} °C");
            }

            // Seuil indicatif (pas de valeur constructeur universelle) : au-delà, on considère le terminal chaud.
            string status = maxTempC >= 55 ? "⚠️" : "✅";
            return new DiagTestResult { Details = sb.ToString(), StatusIcon = status };
        }

        private static List<string> SplitNonEmptyLines(string output) =>
            (output ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        private static readonly string[] CrashAnrTagFragments = { "app_crash", "app_anr", "app_native_crash" };

        private async Task<DiagTestResult> CheckCrashHistoryAsync(string serial)
        {
            var res = await _adbManager.ExecuteAsync($"-s {serial} shell dumpsys dropbox");
            return ParseCrashHistory(res.StandardOutput);
        }

        /// <summary>
        /// Fonction pure, testable : filtre l'historique système (DropBox) pour n'en garder que les entrées
        /// de plantage applicatif (crash) ou de gel (ANR), sans passer par un Bugreport complet.
        /// </summary>
        public static DiagTestResult ParseCrashHistory(string dropboxOutput)
        {
            var lines = (dropboxOutput ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var relevant = lines.Where(l => CrashAnrTagFragments.Any(tag => l.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            if (relevant.Count == 0)
            {
                return new DiagTestResult
                {
                    Details = "Aucun plantage applicatif (crash) ni gel (ANR) récent dans l'historique système du terminal.",
                    StatusIcon = "✅"
                };
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"--- {relevant.Count} ÉVÉNEMENT(S) RÉCENT(S) (CRASH / ANR) ---");
            // Les entrées les plus récentes sont généralement en fin de liste (ordre chronologique d'insertion).
            foreach (var line in relevant.Skip(Math.Max(0, relevant.Count - 15)))
            {
                sb.AppendLine($"• {line.Trim()}");
            }
            sb.AppendLine();
            sb.AppendLine("ℹ️ Pour l'analyse complète d'un de ces événements, générez un Bugreport depuis l'onglet Logs.");

            return new DiagTestResult { Details = sb.ToString(), StatusIcon = "⚠️" };
        }

        /// <summary>Construit le JSON du rapport RMA. Fonction pure, testable.</summary>
        public static string BuildRmaReportJson(string serial, IEnumerable<DiagComponent> components)
        {
            // Sérialisation par System.Text.Json plutôt qu'à la main : l'ancienne version n'échappait ni les
            // antislashs, ni les tabulations, ni les caractères de contrôle (fréquents dans les sorties
            // "dumpsys dropbox"), ce qui pouvait produire un fichier JSON invalide. Mêmes clés, même ordre.
            var report = new
            {
                Appareil = serial ?? "",
                Date_Test = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Composants = (components ?? Enumerable.Empty<DiagComponent>()).Select(item => new
                {
                    Id = item.SystemId ?? "",
                    Nom = item.ComponentName ?? "",
                    Statut = item.StatusIcon ?? "",
                    Details = (item.Details ?? "").Replace("\r", ""),
                    Commentaire = (item.Comment ?? "").Replace("\r", "")
                }).ToList()
            };

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                // Garde les accents et émojis lisibles dans le fichier (sinon "é"...). Sans risque ici :
                // le JSON est écrit sur disque, jamais injecté dans une page HTML.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            return System.Text.Json.JsonSerializer.Serialize(report, options);
        }
    }

    // --- LABORATOIRE MATÉRIEL (GETEVENT) ---

    public enum GetEventKind { None, KeyPress, TouchX, TouchY, SyncReport }

    /// <summary>Résultat du classement d'une ligne brute de "getevent -l".</summary>
    public struct GetEventResult
    {
        public GetEventKind Kind;
        public string RawKeyName;
        public string DisplayKeyName;
        public int Value;
    }

    /// <summary>Parsing des lignes "getevent -l" (boutons physiques, coordonnées tactiles). Fonction pure, testable.</summary>
    public static class GetEventParser
    {
        public static GetEventResult Parse(string line)
        {
            line = line ?? "";

            if (line.Contains("EV_KEY"))
            {
                // La Regex capture le nom de la touche (Groupe 1) et son état (Groupe 2)
                // Exemple texte : /dev/input/event0: EV_KEY       KEY_VOLUMEUP         DOWN
                // Exemple hexa  : /dev/input/event0: EV_KEY       0114                 00000001
                var matchKey = Regex.Match(line, @"EV_KEY\s+([^\s]+)\s+([^\s]+)");
                if (matchKey.Success)
                {
                    string keyName = matchKey.Groups[1].Value;
                    string state = matchKey.Groups[2].Value;

                    // On ne garde que les "Appuis" (état DOWN ou sa valeur Hexa 00000001)
                    // Et on ignore volontairement "BTN_TOUCH" pour que l'écran tactile ne pollue pas les boutons physiques
                    if ((state == "DOWN" || state == "00000001" || state == "1") && keyName != "BTN_TOUCH")
                    {
                        return new GetEventResult
                        {
                            Kind = GetEventKind.KeyPress,
                            RawKeyName = keyName,
                            DisplayKeyName = keyName.Replace("KEY_", "").Replace("BTN_", "")
                        };
                    }
                }
                return new GetEventResult { Kind = GetEventKind.None };
            }

            if (line.Contains("ABS_MT_POSITION_X"))
            {
                var matchX = Regex.Match(line, @"ABS_MT_POSITION_X\s+([0-9a-fA-F]+)");
                if (matchX.Success)
                {
                    return new GetEventResult { Kind = GetEventKind.TouchX, Value = Convert.ToInt32(matchX.Groups[1].Value, 16) };
                }
            }
            else if (line.Contains("ABS_MT_POSITION_Y"))
            {
                var matchY = Regex.Match(line, @"ABS_MT_POSITION_Y\s+([0-9a-fA-F]+)");
                if (matchY.Success)
                {
                    return new GetEventResult { Kind = GetEventKind.TouchY, Value = Convert.ToInt32(matchY.Groups[1].Value, 16) };
                }
            }

            // Fin d'une trame d'événements tactiles : X et Y sont alors cohérents entre eux.
            if (line.Contains("SYN_REPORT"))
            {
                return new GetEventResult { Kind = GetEventKind.SyncReport };
            }

            return new GetEventResult { Kind = GetEventKind.None };
        }
    }
}
