using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Lecture de logs reçus sous forme de fichier : logcat enregistré (.txt / .log, formats threadtime, time,
    /// brief, avec ou sans année / UID) ou bugreport Android (.zip, ou son .txt). Les lignes non reconnues d'un
    /// fichier logcat sont gardées telles quelles (suite d'un message sur plusieurs lignes) ; dans un bugreport,
    /// seules les lignes de log sont gardées (le reste est l'état du système : dumpsys, propriétés…).
    /// </summary>
    public static class LogImport
    {
        private static readonly Regex ThreadTimeExt = new Regex(
            @"^(?:\d{4}-)?(\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}\.\d{3})\d*\s+(?:[\w.]+\s+)?(\d+)\s+\d+\s+([VDIWEFA])\s+(.*?)\s*:\s?(.*)$", RegexOptions.Compiled);
        private static readonly Regex TimeFormat = new Regex(
            @"^(?:\d{4}-)?(\d{2}-\d{2}\s\d{2}:\d{2}:\d{2}\.\d{3})\d*\s+([VDIWEFA])/(.*?)\(\s*(\d+)\):\s?(.*)$", RegexOptions.Compiled);
        private static readonly Regex BriefFormat = new Regex(
            @"^([VDIWEFA])/(.*?)\(\s*(\d+)\):\s?(.*)$", RegexOptions.Compiled);

        /// <summary>Taille maximale lue dans un fichier (les bugreports dépassent souvent 100 Mo).</summary>
        public const long MaxBytes = 400L * 1024 * 1024;

        /// <summary>Reconnaît une ligne de log quel que soit son format ; Time vide si ce n'en est pas une.</summary>
        public static LogEntry ParseAny(string line)
        {
            line = line ?? "";
            var e = LogcatService.ParseLine(line);
            if (!string.IsNullOrEmpty(e.Time)) return e;

            var m = ThreadTimeExt.Match(line);
            if (m.Success)
                return new LogEntry { Time = m.Groups[1].Value, PID = m.Groups[2].Value, Level = Lvl(m.Groups[3].Value), Tag = m.Groups[4].Value.Trim(), Message = m.Groups[5].Value, RawLine = line };
            m = TimeFormat.Match(line);
            if (m.Success)
                return new LogEntry { Time = m.Groups[1].Value, PID = m.Groups[4].Value, Level = Lvl(m.Groups[2].Value), Tag = m.Groups[3].Value.Trim(), Message = m.Groups[5].Value, RawLine = line };
            m = BriefFormat.Match(line);
            if (m.Success)
                return new LogEntry { Time = "", PID = m.Groups[3].Value, Level = Lvl(m.Groups[1].Value), Tag = m.Groups[2].Value.Trim(), Message = m.Groups[4].Value, RawLine = line };
            return e;
        }

        private static string Lvl(string l) => l == "A" ? "F" : l;

        private static bool IsLogLine(LogEntry e) => !string.IsNullOrEmpty(e.Time) || !string.IsNullOrEmpty(e.Tag);

        /// <summary>Lignes de texte → entrées. <paramref name="logLinesOnly"/> : ignorer ce qui n'est pas du log.</summary>
        public static List<LogEntry> ParseLines(IEnumerable<string> lines, bool logLinesOnly)
        {
            var list = new List<LogEntry>();
            foreach (var raw in lines)
            {
                if (raw == null) continue;
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith("--------- beginning of", StringComparison.Ordinal)) continue;
                var e = ParseAny(line);
                if (logLinesOnly && !IsLogLine(e)) continue;
                list.Add(e);
            }
            return list;
        }

        /// <summary>Lit un fichier de logs (.txt, .log, bugreport .zip). Lève une exception explicite sinon.</summary>
        public static List<LogEntry> LoadFile(string path)
        {
            LastImportNote = "";
            if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var rx = LoadRxLogger(zip);
                    if (rx != null) return rx;

                    // Le rapport principal : bugreport-*.txt (le plus gros .txt à la racine), sinon le plus gros .txt.
                    var txt = zip.Entries.Where(z => z.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
                    var main = txt.Where(z => z.Name.StartsWith("bugreport", StringComparison.OrdinalIgnoreCase) && !z.FullName.Contains("/"))
                                  .OrderByDescending(z => z.Length).FirstOrDefault()
                               ?? txt.OrderByDescending(z => z.Length).FirstOrDefault();
                    if (main == null) throw new InvalidDataException(Loc.T("Aucun fichier de logs (.txt) dans cette archive."));
                    using (var s = main.Open())
                        return ParseLines(ReadLines(s), true);
                }
            }

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Bugreport décompressé : il commence par « ==== dumpstate ».
                var head = new byte[256];
                int n = fs.Read(head, 0, head.Length);
                fs.Position = 0;
                bool bugreport = Encoding.UTF8.GetString(head, 0, n).Contains("dumpstate");
                var list = ParseLines(ReadLines(fs), bugreport);
                if (list.Count > 0 && !list.Any(IsLogLine))
                    throw new InvalidDataException(Loc.T("Ce fichier ne contient pas de lignes de log Android reconnues."));
                return list;
            }
        }

        /// <summary>Volume de logs « Main » lu dans une archive RxLogger (les plus récents d'abord).</summary>
        public const long RxLoggerMainBudget = 110L * 1024 * 1024;

        private static readonly Regex RxLogFile = new Regex(@"^(Main|System|Crash)(\d+)\.txt$", RegexOptions.IgnoreCase);

        /// <summary>Note sur le dernier fichier lu (journaux d'une archive RxLogger retenus), vide sinon.</summary>
        public static string LastImportNote { get; private set; } = "";

        /// <summary>
        /// Archive RxLogger (Zebra) : le journal est découpé en Main0 (le plus récent), Main1… plus System et Crash.
        /// On lit les Main les plus récents dans la limite d'un volume raisonnable, avec System et Crash (petits,
        /// plantages et ANR), puis on remet tout dans l'ordre chronologique. Null si ce n'est pas une archive RxLogger.
        /// </summary>
        private static List<LogEntry> LoadRxLogger(ZipArchive zip)
        {
            var files = zip.Entries.Select(z => new { Entry = z, M = RxLogFile.Match(z.Name) }).Where(x => x.M.Success)
                .Select(x => new { x.Entry, Kind = x.M.Groups[1].Value.ToLowerInvariant(), Index = int.Parse(x.M.Groups[2].Value) }).ToList();
            var mains = files.Where(f => f.Kind == "main").OrderBy(f => f.Index).ToList();
            if (mains.Count == 0 || (mains.Count == 1 && files.Count == 1 && !zip.Entries.Any(z => z.FullName.IndexOf("RxLogger", StringComparison.OrdinalIgnoreCase) >= 0)))
                return null;

            var chosen = new List<ZipArchiveEntry>();
            long total = 0;
            foreach (var m in mains)
            {
                if (chosen.Count > 0 && total + m.Entry.Length > RxLoggerMainBudget) break;
                chosen.Add(m.Entry); total += m.Entry.Length;
            }
            var others = files.Where(f => f.Kind != "main").Select(f => f.Entry).ToList();

            var all = new List<(string Key, int Order, LogEntry Entry)>();
            int order = 0;
            string minKey = null, maxKey = null;
            void Read(ZipArchiveEntry entry, bool isMain)
            {
                using (var s = entry.Open())
                {
                    string lastTime = "";
                    foreach (var e in ParseLines(ReadLines(s), true))
                    {
                        if (!string.IsNullOrEmpty(e.Time)) lastTime = e.Time;
                        if (isMain && lastTime.Length > 0)
                        {
                            if (minKey == null || string.CompareOrdinal(lastTime, minKey) < 0) minKey = lastTime;
                            if (maxKey == null || string.CompareOrdinal(lastTime, maxKey) > 0) maxKey = lastTime;
                        }
                        // System / Crash : seulement la période couverte par les Main retenus (pas d'autres jours,
                        // ni les dates fantaisistes d'avant la mise à l'heure du terminal).
                        if (!isMain && (minKey == null || string.CompareOrdinal(lastTime, minKey) < 0 || string.CompareOrdinal(lastTime, maxKey) > 0)) continue;
                        all.Add((lastTime, order++, e));
                    }
                }
            }
            foreach (var entry in chosen.AsEnumerable().Reverse()) Read(entry, true);
            foreach (var entry in others) Read(entry, false);

            var sorted = all.OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Order).Select(x => x.Entry).ToList();
            string from = minKey ?? "", to = maxKey ?? "";
            string skipped = mains.Count > chosen.Count
                ? " " + Loc.F("({0} journal(aux) plus ancien(s) non lu(s))", mains.Count - chosen.Count) : "";
            LastImportNote = Loc.F("Archive RxLogger : {0} retenu(s), avec System et Crash — du {1} au {2}.", string.Join(", ", chosen.Select(z => z.Name)), from, to) + skipped;
            return sorted;
        }

        private static IEnumerable<string> ReadLines(Stream s)
        {
            long read = 0;
            using (var r = new StreamReader(s, Encoding.UTF8, true))
            {
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    read += line.Length + 1;
                    if (read > MaxBytes) yield break;
                    yield return line;
                }
            }
        }
    }
}
