using System;
using System.Text.RegularExpressions;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Critères de filtrage des logs, capturés une seule fois depuis l'UI plutôt que relus
    /// contrôle par contrôle à chaque ligne.
    /// </summary>
    public struct LogFilterCriteria
    {
        /// <summary>"D", "I", "W" ou "E" (E couvre aussi F/Fatal). Null/vide = tous niveaux.</summary>
        public string LevelFilter;

        /// <summary>PID de l'app au premier plan à isoler. Null/vide = pas de filtre PID.</summary>
        public string ForegroundPid;

        /// <summary>Texte (ou motif regex si UseRegex) recherché dans la ligne brute. Null/vide = pas de filtre texte.</summary>
        public string SearchText;

        /// <summary>Si vrai, SearchText est traité comme une expression régulière plutôt qu'une sous-chaîne simple.</summary>
        public bool UseRegex;
    }

    /// <summary>
    /// Logique de correspondance d'un <see cref="LogEntry"/> à des critères de filtrage.
    /// Fonction pure, sans dépendance UI : testable isolément.
    /// </summary>
    public static class LogFilter
    {
        // Dernier motif regex compilé : le même motif sert pour les milliers de lignes d'un filtrage.
        private static readonly object RegexCacheLock = new object();
        private static string _cachedPattern;
        private static Regex _cachedRegex;

        private static Regex GetCachedRegex(string pattern)
        {
            lock (RegexCacheLock)
            {
                if (pattern != _cachedPattern)
                {
                    _cachedPattern = pattern;
                    try { _cachedRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                    catch (ArgumentException) { _cachedRegex = null; }
                }
                return _cachedRegex;
            }
        }

        public static bool Matches(LogEntry log, LogFilterCriteria criteria)
        {
            if (!string.IsNullOrEmpty(criteria.LevelFilter))
            {
                bool levelOk = criteria.LevelFilter == "E"
                    ? (log.Level == "E" || log.Level == "F")
                    : log.Level == criteria.LevelFilter;
                if (!levelOk) return false;
            }

            if (!string.IsNullOrEmpty(criteria.ForegroundPid) && log.PID != criteria.ForegroundPid)
                return false;

            if (!string.IsNullOrEmpty(criteria.SearchText))
            {
                string line = log.RawLine ?? string.Empty;

                if (criteria.UseRegex)
                {
                    // Motif regex invalide (souvent en cours de frappe) : ne fait rien correspondre plutôt
                    // que de planter — la liste se re-remplira dès que le motif redevient valide.
                    var regex = GetCachedRegex(criteria.SearchText);
                    if (regex == null || !regex.IsMatch(line)) return false;
                }
                else if (line.IndexOf(criteria.SearchText, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Comparaison sans allocation (auparavant deux ToLower() par ligne et par filtrage).
                    return false;
                }
            }

            return true;
        }
    }
}
