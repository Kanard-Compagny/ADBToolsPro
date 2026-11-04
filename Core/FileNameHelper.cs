using System.IO;
using System.Linq;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Rend une valeur (numéro de série, modèle...) utilisable dans un nom de fichier Windows. Indispensable
    /// pour les terminaux connectés en ADB Wi-Fi, dont le "serial" est de la forme "192.168.1.20:5555" :
    /// le ':' est interdit dans un nom de fichier et faisait échouer captures, vidéos, logs et rapports.
    /// </summary>
    public static class FileNameHelper
    {
        // Liste Windows explicite en plus de celle du système, pour un résultat identique quel que soit l'OS.
        private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars()
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            .Distinct()
            .ToArray();

        public static string Sanitize(string value, string fallback = "Inconnu")
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            var chars = value.Trim().Select(c => InvalidChars.Contains(c) ? '_' : c).ToArray();
            string result = new string(chars).TrimEnd('.', ' ');
            return string.IsNullOrEmpty(result) ? fallback : result;
        }
    }
}
