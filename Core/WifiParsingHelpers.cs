using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Logique de repli partagée pour la résolution du SSID, utilisée à la fois par RadarService et
    /// DiagnosticService : sur certains terminaux (notamment Zebra), "dumpsys wifi" remonte un SSID
    /// vide ou générique ("101") alors que l'appareil est bel et bien connecté. "cmd wifi status" donne
    /// alors une valeur fiable.
    /// </summary>
    public static class WifiParsingHelpers
    {
        private static readonly Regex CmdWifiConnectedRegex = new Regex(@"connected to\s*""([^""]+)""");

        public static bool NeedsSsidFallback(string ssid) =>
            string.IsNullOrEmpty(ssid) || ssid == "101" || ssid == "Inconnu" || ssid == "<unknown ssid>";

        /// <summary>Extrait le SSID depuis la sortie de "cmd wifi status". Retourne null si non trouvé.</summary>
        public static string ExtractSsidFromCmdWifiStatus(string cmdWifiStatusOutput)
        {
            var m = CmdWifiConnectedRegex.Match(cmdWifiStatusOutput ?? "");
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Portion de "dumpsys wifi" décrivant la connexion en cours : la ligne "mWifiInfo" quand elle existe.
        /// Ailleurs dans le dump (historique, état du supplicant…), on trouve sous Android 11 des lignes du type
        /// "SSID: Reseau BSSID: 00:00:00:00:00:00 nid: 1 state: ASSOCIATED" qui faussaient le SSID et le BSSID.
        /// </summary>
        public static string WifiInfoScope(string dump)
        {
            foreach (var line in (dump ?? "").Split('\n'))
                if (line.Contains("mWifiInfo") && line.Contains("SSID:")) return line;
            return dump ?? "";
        }

        private static readonly Regex SsidRegex = new Regex(@"(?<!B)SSID:\s*(?:""([^""]*)""|([^,\r\n]*?))(?=\s+BSSID:|,|\r|\n|$)");
        private static readonly Regex BssidRegex = new Regex(@"BSSID:\s*([0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5})");

        /// <summary>SSID (sans guillemets, coupé avant un éventuel "BSSID:" collé sur la même ligne), ou null.</summary>
        public static string ExtractSsid(string text)
        {
            var m = SsidRegex.Match(text ?? "");
            if (!m.Success) return null;
            string v = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            return v.Length == 0 ? null : v;
        }

        /// <summary>Premier BSSID réel (les adresses nulles 00:00… ou masquées 02:00:00:00:00:00 sont ignorées), ou null.</summary>
        public static string ExtractBssid(string text)
        {
            foreach (Match m in BssidRegex.Matches(text ?? ""))
            {
                string v = m.Groups[1].Value.ToLowerInvariant();
                if (v != "00:00:00:00:00:00" && v != "02:00:00:00:00:00" && v != "ff:ff:ff:ff:ff:ff") return m.Groups[1].Value;
            }
            return null;
        }
    }
}
