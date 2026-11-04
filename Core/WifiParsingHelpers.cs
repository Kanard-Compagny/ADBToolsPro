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
    }
}
