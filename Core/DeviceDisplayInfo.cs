using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Résolution de la résolution d'écran active d'un terminal ("wm size"), partagée entre le test d'écran
    /// (mires plein écran) et la grille de zones tactiles : les deux ont besoin du ratio réel de l'appareil.
    /// </summary>
    public static class DeviceDisplayInfo
    {
        private static readonly Regex OverrideSizeRegex = new Regex(@"Override size:\s*(\d+)x(\d+)");
        private static readonly Regex PhysicalSizeRegex = new Regex(@"Physical size:\s*(\d+)x(\d+)");

        /// <summary>
        /// Résout la résolution active (tient compte d'un "Override size" en cours, sinon la résolution
        /// physique). Repli sur 1080x1920 si la commande échoue ou renvoie un format inattendu.
        /// </summary>
        public static async Task<(int Width, int Height)> ResolveScreenSizeAsync(AdbManager adbManager, string serial)
        {
            var res = await adbManager.ExecuteAsync($"-s {serial} shell wm size");
            return ParseScreenSize(res.StandardOutput);
        }

        public static (int Width, int Height) ParseScreenSize(string wmSizeOutput)
        {
            string output = wmSizeOutput ?? "";

            var match = OverrideSizeRegex.Match(output);
            if (!match.Success) match = PhysicalSizeRegex.Match(output);

            if (match.Success && int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
            {
                return (w, h);
            }

            return (1080, 1920);
        }

        private static readonly Regex OverrideDensityRegex = new Regex(@"Override density:\s*(\d+)");
        private static readonly Regex PhysicalDensityRegex = new Regex(@"Physical density:\s*(\d+)");

        /// <summary>Résout la densité DPI active (Override si présent, sinon Physical). Retourne null si indisponible.</summary>
        public static async Task<(int Dpi, bool IsOverride)?> ResolveDensityAsync(AdbManager adbManager, string serial)
        {
            var res = await adbManager.ExecuteAsync($"-s {serial} shell wm density");
            return ParseDensity(res.StandardOutput);
        }

        public static (int Dpi, bool IsOverride)? ParseDensity(string wmDensityOutput)
        {
            string output = wmDensityOutput ?? "";

            var matchOverride = OverrideDensityRegex.Match(output);
            if (matchOverride.Success && int.TryParse(matchOverride.Groups[1].Value, out int dpiOverride))
                return (dpiOverride, true);

            var matchPhysical = PhysicalDensityRegex.Match(output);
            if (matchPhysical.Success && int.TryParse(matchPhysical.Groups[1].Value, out int dpiPhysical))
                return (dpiPhysical, false);

            return null;
        }
    }
}
