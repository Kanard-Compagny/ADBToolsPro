using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ADBToolsNew.Core
{
    /// <summary>Nature d'un fichier trouvé dans un relevé.</summary>
    public enum ArchiveFileKind { Log, Anr, Tombstone, Snapshot, Recording, Text }

    /// <summary>Un fichier du relevé (journal, rapport ANR, tombstone, snapshot…).</summary>
    public sealed class ArchiveFile : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        /// <summary>Chemin dans l'archive ou sur le disque.</summary>
        public string FullName { get; set; } = "";
        public ArchiveFileKind Kind { get; set; }
        public long Size { get; set; }
        public int LineCount { get; set; }
        public int SourceId { get; set; } = -1;
        public string FirstTime { get; set; } = "";
        public string LastTime { get; set; } = "";

        private bool _included = true;
        /// <summary>Journal affiché (case cochée dans la liste des sources).</summary>
        public bool Included
        {
            get => _included;
            set { if (_included == value) return; _included = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Included))); }
        }

        public bool IsLog => Kind == ArchiveFileKind.Log || Kind == ArchiveFileKind.Recording;
        public string Icon
        {
            get
            {
                switch (Kind)
                {
                    case ArchiveFileKind.Log: return "📜";
                    case ArchiveFileKind.Recording: return "🎙";
                    case ArchiveFileKind.Anr: return "🧊";
                    case ArchiveFileKind.Tombstone: return "💥";
                    case ArchiveFileKind.Snapshot: return "📸";
                    default: return "📄";
                }
            }
        }
        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case ArchiveFileKind.Log: return Loc.T("Journal");
                    case ArchiveFileKind.Recording: return Loc.T("Enregistrement");
                    case ArchiveFileKind.Anr: return Loc.T("Application figée (ANR)");
                    case ArchiveFileKind.Tombstone: return Loc.T("Plantage natif (tombstone)");
                    case ArchiveFileKind.Snapshot: return Loc.T("État du système");
                    default: return Loc.T("Fichier texte");
                }
            }
        }
        public string SizeText => Size >= 1024 * 1024 ? (Size / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " Mo"
                                : Size >= 1024 ? (Size / 1024.0).ToString("0", CultureInfo.CurrentCulture) + " Ko" : Size + " o";
        public string InfoText => IsLog
            ? Loc.F("{0} lignes", LineCount.ToString("N0", CultureInfo.CurrentCulture)) + (FirstTime.Length > 0 ? " · " + LogFinding.ShortTime(FirstTime) + " → " + LogFinding.ShortTime(LastTime) : "")
            : KindText + " · " + SizeText;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>Ligne de journal d'un relevé : avec son fichier d'origine et sa position dans le temps.</summary>
    public sealed class FileLogEntry : LogEntry
    {
        public string Source { get; set; } = "";
        public int SourceId { get; set; }
        /// <summary>Minutes depuis le début de l'année (MM-dd HH:mm), -1 sans heure : sert à la frise et aux périodes.</summary>
        public int Minute { get; set; } = -1;
        /// <summary>Position dans le relevé complet (ordre chronologique).</summary>
        public int Index { get; set; }
    }

    /// <summary>Critères de filtrage de la page d'analyse.</summary>
    public sealed class ArchiveFilter
    {
        public string Text = "";
        public bool Regex;
        public HashSet<string> Levels;           // null = tous
        public string Tag = "";
        public string Pid = "";
        public HashSet<int> Sources;             // null = tous
        public int FromMinute = int.MinValue, ToMinute = int.MaxValue;

        public bool IsEmpty => string.IsNullOrEmpty(Text) && Levels == null && string.IsNullOrEmpty(Tag) && string.IsNullOrEmpty(Pid)
                               && Sources == null && FromMinute == int.MinValue && ToMinute == int.MaxValue;
    }

    /// <summary>
    /// Relevé de logs complet ouvert dans la page « Analyse de fichiers » : archive RxLogger (Zebra), bugreport,
    /// dossier décompressé ou simple logcat. Tous les journaux datés sont fusionnés dans l'ordre chronologique ;
    /// les autres fichiers utiles (ANR, tombstones, snapshots, journal noyau…) restent consultables tels quels.
    /// </summary>
    public sealed class LogArchive
    {
        /// <summary>Limite de lignes chargées (au-delà, les journaux les plus anciens sont laissés de côté).</summary>
        public const int MaxLines = 6000000;
        private const long MaxTextFile = 600L * 1024 * 1024;

        public string Path { get; private set; } = "";
        public string Title { get; private set; } = "";
        public List<ArchiveFile> Files { get; } = new List<ArchiveFile>();
        public List<FileLogEntry> Entries { get; private set; } = new List<FileLogEntry>();
        /// <summary>Informations sur le terminal (modèle, Android, build…) retrouvées dans le relevé.</summary>
        public List<KeyValuePair<string, string>> Device { get; } = new List<KeyValuePair<string, string>>();
        public string Note { get; private set; } = "";
        private bool _isZip, _isFolder;

        public IEnumerable<ArchiveFile> LogFiles => Files.Where(f => f.IsLog);
        public IEnumerable<ArchiveFile> OtherFiles => Files.Where(f => !f.IsLog);
        private int _spanFrom = -2, _spanTo = -2;
        /// <summary>
        /// Période réelle du relevé (minutes), sans les quelques lignes datées d'avant la mise à l'heure du terminal
        /// (01/01, 02/03…) : sert au résumé et à la frise.
        /// </summary>
        public int SpanFrom { get { ComputeSpan(); return _spanFrom; } }
        public int SpanTo { get { ComputeSpan(); return _spanTo; } }
        private void ComputeSpan()
        {
            if (_spanFrom != -2) return;
            var m = Entries.Where(e => e.Minute >= 0).Select(e => e.Minute).ToList();
            if (m.Count == 0) { _spanFrom = _spanTo = -1; return; }
            m.Sort();
            _spanFrom = m[(int)(m.Count * 0.002)];
            _spanTo = m[Math.Min(m.Count - 1, (int)(m.Count * 0.9995))];
        }

        /// <summary>Lignes datées entre deux minutes (incluses), journaux choisis seulement (null = tous).</summary>
        public List<FileLogEntry> Range(int from, int to, HashSet<int> sources = null) =>
            Entries.Where(e => e.Minute >= from && e.Minute <= to && (sources == null || sources.Contains(e.SourceId))).ToList();

        /// <summary>Lignes qui entourent une ligne du relevé (contexte d'un constat).</summary>
        public List<FileLogEntry> Around(int index, int before = 8, int after = 8)
        {
            int a = Math.Max(0, index - before), b = Math.Min(Entries.Count - 1, index + after);
            return a > b ? new List<FileLogEntry>() : Entries.GetRange(a, b - a + 1);
        }

        public string FirstTime => Entries.FirstOrDefault(e => e.Minute >= 0)?.Time ?? "";
        public string LastTime => Entries.LastOrDefault(e => e.Minute >= 0)?.Time ?? "";

        // ------------------------------------------------------------------ Ouverture

        private static readonly Regex RxNumbered = new Regex(@"^(Main|System|Crash|Radio|Event|Events|Kernel)(\d+)\.txt$", RegexOptions.IgnoreCase);
        private static readonly Regex DeviceHeader = new Regex(@"DEVICE:\s*(?<dev>\S+)\s+SERIAL NUMBER:\s*(?<sn>\S*)\s+ANDROID VERSION:\s*(?<av>\S+)\s+BUILD ID:\s*(?<b>\S+)", RegexOptions.IgnoreCase);
        private static readonly Regex PropLine = new Regex(@"^\[([\w.\-]+)\]:\s*\[(.*)\]\s*$");

        /// <summary>Ouvre un .zip, un dossier ou un fichier texte.</summary>
        public static LogArchive Open(string path, IProgress<string> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var a = new LogArchive { Path = path, Title = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) };
            if (Directory.Exists(path)) { a._isFolder = true; a.LoadFolder(path, progress, ct); }
            else if (string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)) { a._isZip = true; a.LoadZip(path, progress, ct); }
            else a.LoadSingle(path, progress, ct);
            return a;
        }

        private sealed class Candidate
        {
            public string Name, FullName; public long Size; public Func<Stream> Open; public ArchiveFileKind Kind;
            public string Family; public int Number = -1;
        }

        private void LoadZip(string path, IProgress<string> progress, CancellationToken ct)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var list = zip.Entries.Where(z => z.Length > 0 && z.Name.Length > 0)
                    .Select(z => new Candidate { Name = z.Name, FullName = z.FullName, Size = z.Length, Open = z.Open }).ToList();
                LoadCandidates(list, progress, ct);
            }
        }

        private void LoadFolder(string path, IProgress<string> progress, CancellationToken ct)
        {
            var list = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).Where(f => f.Length > 0)
                .Select(f => new Candidate
                {
                    Name = f.Name, FullName = f.FullName.Substring(path.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/'),
                    Size = f.Length, Open = () => new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                }).ToList();
            LoadCandidates(list, progress, ct);
        }

        private void LoadSingle(string path, IProgress<string> progress, CancellationToken ct)
        {
            var f = new FileInfo(path);
            var c = new Candidate { Name = f.Name, FullName = f.Name, Size = f.Length, Open = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) };
            LoadCandidates(new List<Candidate> { c }, progress, ct, forceLog: true);
        }

        private void LoadCandidates(List<Candidate> all, IProgress<string> progress, CancellationToken ct, bool forceLog = false)
        {
            foreach (var c in all) Classify(c, forceLog);
            var logs = all.Where(c => c.Kind == ArchiveFileKind.Log || c.Kind == ArchiveFileKind.Recording).ToList();
            // Les plus récents d'abord (Main0 avant Main1…) pour rester sous la limite si le relevé est énorme.
            logs = logs.OrderBy(c => c.Number < 0 ? 0 : c.Number).ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase).ToList();

            var parsed = new List<(ArchiveFile File, List<LogEntry> Lines)>();
            int total = 0, skipped = 0;
            foreach (var c in logs)
            {
                ct.ThrowIfCancellationRequested();
                if (total >= MaxLines) { skipped++; continue; }
                progress?.Report(Loc.F("Lecture de {0}…", c.FullName));
                var lines = new List<LogEntry>();
                string firstLine = null;
                using (var s = c.Open())
                using (var r = new StreamReader(s, Encoding.UTF8, true))
                {
                    string line; long read = 0;
                    while ((line = r.ReadLine()) != null)
                    {
                        if (firstLine == null) firstLine = line;
                        read += line.Length + 1;
                        if (read > MaxTextFile) break;
                        if (line.Length == 0 || line.StartsWith("--------- beginning of", StringComparison.Ordinal)) continue;
                        var e = LogImport.ParseAny(line.TrimEnd('\r'));
                        if (string.IsNullOrEmpty(e.Time) && string.IsNullOrEmpty(e.Tag)) continue; // pas une ligne de log
                        e.RawLine = null; // inutile ici, et double la mémoire
                        lines.Add(e);
                        if (lines.Count % 200000 == 0) { ct.ThrowIfCancellationRequested(); progress?.Report(Loc.F("Lecture de {0}… {1} lignes", c.FullName, lines.Count.ToString("N0", CultureInfo.CurrentCulture))); }
                    }
                }
                if (firstLine != null) ReadDeviceHeader(firstLine);
                if (lines.Count == 0 && !forceLog) { c.Kind = ArchiveFileKind.Text; continue; }
                var file = new ArchiveFile { Name = c.Name, FullName = c.FullName, Kind = c.Kind, Size = c.Size, LineCount = lines.Count, SourceId = parsed.Count };
                parsed.Add((file, lines));
                total += lines.Count;
            }

            // Fusion chronologique (tri stable : à heure égale, l'ordre du fichier est gardé ; une ligne sans heure
            // reste derrière la précédente).
            progress?.Report(Loc.T("Fusion des journaux dans l'ordre chronologique…"));
            var merged = new List<(string Key, int Src, int Seq, LogEntry E)>(total);
            foreach (var p in parsed)
            {
                string last = "";
                for (int i = 0; i < p.Lines.Count; i++)
                {
                    var e = p.Lines[i];
                    if (!string.IsNullOrEmpty(e.Time)) last = e.Time;
                    merged.Add((last, p.File.SourceId, i, e));
                }
            }
            merged.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.Key, y.Key);
                if (c != 0) return c;
                c = x.Src.CompareTo(y.Src);
                return c != 0 ? c : x.Seq.CompareTo(y.Seq);
            });

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var entries = new List<FileLogEntry>(merged.Count);
            foreach (var m in merged)
            {
                var e = m.E;
                string tag = e.Tag ?? "";
                if (!tags.TryGetValue(tag, out var it)) { tags[tag] = tag; it = tag; }
                var f = parsed[m.Src].File;
                entries.Add(new FileLogEntry
                {
                    Time = e.Time ?? "", PID = e.PID ?? "", Level = string.IsInterned(e.Level ?? "") ?? e.Level, Tag = it, Message = e.Message ?? "",
                    Source = f.Name, SourceId = f.SourceId, Minute = ToMinute(m.Key), Index = entries.Count,
                });
            }
            Entries = entries;
            foreach (var p in parsed)
            {
                var first = p.Lines.FirstOrDefault(x => !string.IsNullOrEmpty(x.Time));
                var last = p.Lines.LastOrDefault(x => !string.IsNullOrEmpty(x.Time));
                p.File.FirstTime = first?.Time ?? ""; p.File.LastTime = last?.Time ?? "";
                Files.Add(p.File);
            }
            foreach (var c in all.Where(c => c.Kind != ArchiveFileKind.Log && c.Kind != ArchiveFileKind.Recording && c.Kind != (ArchiveFileKind)(-1))
                                 .OrderBy(c => c.Kind).ThenBy(c => c.FullName, StringComparer.OrdinalIgnoreCase))
                Files.Add(new ArchiveFile { Name = c.Name, FullName = c.FullName, Kind = c.Kind, Size = c.Size, Included = false });

            ReadSnapshotProps(all);
            Note = skipped > 0 ? Loc.F("{0} journal(aux) plus ancien(s) non chargé(s) (limite de {1} lignes).", skipped, MaxLines.ToString("N0", CultureInfo.CurrentCulture)) : "";
        }

        private static readonly HashSet<string> BinaryExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".mp4", ".zip", ".gz", ".apk", ".so", ".bin", ".db", ".pb", ".id", ".dat", ".img", ".wav", ".mp3" };

        private static void Classify(Candidate c, bool forceLog)
        {
            string name = c.Name, full = c.FullName.Replace('\\', '/'), lower = full.ToLowerInvariant();
            string ext = System.IO.Path.GetExtension(name);
            var m = RxNumbered.Match(name);
            if (m.Success) { c.Family = m.Groups[1].Value.ToLowerInvariant(); c.Number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }

            if (forceLog) { c.Kind = ArchiveFileKind.Log; return; }
            if (BinaryExt.Contains(ext) || name.EndsWith(".idsig", StringComparison.OrdinalIgnoreCase)) { c.Kind = (ArchiveFileKind)(-1); return; }
            if (lower.EndsWith(".jsonl") && lower.Contains("record")) { c.Kind = ArchiveFileKind.Recording; return; }
            if (lower.Contains("/anr/") || name.StartsWith("anr_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("temp_anr", StringComparison.OrdinalIgnoreCase) || name.StartsWith("traces", StringComparison.OrdinalIgnoreCase)) { c.Kind = ArchiveFileKind.Anr; return; }
            if (lower.Contains("tombstone")) { c.Kind = ArchiveFileKind.Tombstone; return; }
            if (lower.Contains("snapshot") || lower.Contains("/memory/") || name.StartsWith("memory", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dumpstate", StringComparison.OrdinalIgnoreCase)) { c.Kind = ArchiveFileKind.Snapshot; return; }
            // Journal noyau : horodaté depuis le démarrage, impossible à remettre dans le temps des autres journaux.
            if (c.Family == "kernel" || name.StartsWith("klog", StringComparison.OrdinalIgnoreCase) || name.StartsWith("dmesg", StringComparison.OrdinalIgnoreCase) || name.StartsWith("kmsg", StringComparison.OrdinalIgnoreCase)) { c.Kind = ArchiveFileKind.Text; return; }
            if (c.Family != null) { c.Kind = ArchiveFileKind.Log; return; }
            if (name.StartsWith("bugreport", StringComparison.OrdinalIgnoreCase) && ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)) { c.Kind = ArchiveFileKind.Log; return; }
            if (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Length == 0)
            {
                c.Kind = LooksLikeLog(c) ? ArchiveFileKind.Log : ArchiveFileKind.Text;
                return;
            }
            c.Kind = c.Size < 20 * 1024 * 1024 && (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".json", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) || ext.Equals(".prop", StringComparison.OrdinalIgnoreCase))
                ? ArchiveFileKind.Text : (ArchiveFileKind)(-1);
        }

        /// <summary>Un fichier texte quelconque est un journal si une bonne part de ses premières lignes en est.</summary>
        private static bool LooksLikeLog(Candidate c)
        {
            try
            {
                using (var s = c.Open())
                using (var r = new StreamReader(s, Encoding.UTF8, true))
                {
                    int lines = 0, logs = 0; string line;
                    while (lines < 200 && (line = r.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        lines++;
                        if (!string.IsNullOrEmpty(LogImport.ParseAny(line).Time)) logs++;
                    }
                    return lines > 0 && logs >= Math.Min(5, lines) && logs * 3 >= lines;
                }
            }
            catch { return false; }
        }

        private void ReadDeviceHeader(string line)
        {
            if (Device.Count > 0) return;
            var m = DeviceHeader.Match(line ?? "");
            if (!m.Success) return;
            var parts = m.Groups["dev"].Value.Split('/');
            AddDevice(Loc.T("Terminal"), parts[0] + (parts.Length > 1 ? " " + parts[1] : ""));
            if (m.Groups["sn"].Value.Length > 0) AddDevice(Loc.T("N° de série"), m.Groups["sn"].Value);
            AddDevice(Loc.T("Version Android"), m.Groups["av"].Value);
            AddDevice(Loc.T("Build"), m.Groups["b"].Value);
        }

        private void AddDevice(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Device.Any(p => p.Key == key)) return;
            Device.Add(new KeyValuePair<string, string>(key, value.Trim()));
        }

        /// <summary>Propriétés utiles (getprop) d'un snapshot ou d'un bugreport, pour la fiche terminal.</summary>
        private void ReadSnapshotProps(List<Candidate> all)
        {
            var wanted = new[]
            {
                ("ro.product.manufacturer", "Fabricant"), ("ro.product.model", "Modèle"), ("ro.serialno", "N° de série"),
                ("ro.build.version.release", "Version Android"), ("ro.build.version.security_patch", "Correctif de sécurité"),
                ("ro.build.display.id", "Build"), ("persist.sys.timezone", "Fuseau horaire"), ("persist.sys.locale", "Langue"),
            };
            var source = all.Where(c => c.Kind == ArchiveFileKind.Snapshot || (c.Name.StartsWith("bugreport", StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(c => c.Number < 0 ? 0 : c.Number).ThenBy(c => c.Name).FirstOrDefault();
            if (source == null) return;
            try
            {
                var found = new Dictionary<string, string>();
                using (var s = source.Open())
                using (var r = new StreamReader(s, Encoding.UTF8, true))
                {
                    string line; int n = 0;
                    while ((line = r.ReadLine()) != null && n++ < 3000000 && found.Count < wanted.Length)
                    {
                        if (line.Length < 6 || line[0] != '[') continue;
                        var m = PropLine.Match(line);
                        if (m.Success && wanted.Any(w => w.Item1 == m.Groups[1].Value) && !found.ContainsKey(m.Groups[1].Value)) found[m.Groups[1].Value] = m.Groups[2].Value;
                    }
                }
                foreach (var w in wanted)
                    if (found.TryGetValue(w.Item1, out var v)) AddDevice(Loc.T(w.Item2), v);
            }
            catch { /* fiche facultative */ }
        }

        /// <summary>Contenu d'un fichier annexe (tronqué au-delà de <paramref name="maxChars"/> caractères).</summary>
        public string ReadText(ArchiveFile f, int maxChars = 4 * 1024 * 1024)
        {
            if (f == null) return "";
            Func<Stream> open = null;
            ZipArchive zip = null;
            try
            {
                if (_isZip)
                {
                    zip = ZipFile.OpenRead(Path);
                    var entry = zip.Entries.FirstOrDefault(z => z.FullName == f.FullName);
                    if (entry == null) return "";
                    open = entry.Open;
                }
                else if (_isFolder) { string p = System.IO.Path.Combine(Path, f.FullName.Replace('/', System.IO.Path.DirectorySeparatorChar)); open = () => File.OpenRead(p); }
                else open = () => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                using (var s = open())
                using (var r = new StreamReader(s, Encoding.UTF8, true))
                {
                    var buf = new char[maxChars];
                    int n = r.ReadBlock(buf, 0, buf.Length);
                    string text = new string(buf, 0, n);
                    if (!r.EndOfStream) text += "\n\n" + Loc.F("… (fichier tronqué à {0} caractères)", maxChars.ToString("N0", CultureInfo.CurrentCulture));
                    return text;
                }
            }
            finally { zip?.Dispose(); }
        }

        // ------------------------------------------------------------------ Temps

        private static readonly int[] MonthStart = { 0, 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };

        /// <summary>« MM-dd HH:mm:ss.fff » → minutes depuis le début de l'année (-1 si illisible).</summary>
        public static int ToMinute(string time)
        {
            if (string.IsNullOrEmpty(time) || time.Length < 11 || time[2] != '-' || time[5] != ' ' || time[8] != ':') return -1;
            int mo = D(time, 0), d = D(time, 3), h = D(time, 6), mi = D(time, 9);
            if (mo < 1 || mo > 12 || d < 1 || d > 31 || h < 0 || h > 23 || mi < 0 || mi > 59) return -1;
            return ((MonthStart[mo] + d - 1) * 24 + h) * 60 + mi;
        }

        private static int D(string s, int i)
        {
            char a = s[i], b = s[i + 1];
            if (a < '0' || a > '9' || b < '0' || b > '9') return -100;
            return (a - '0') * 10 + (b - '0');
        }

        /// <summary>Minutes depuis le début de l'année → « dd/MM HH:mm ».</summary>
        public static string MinuteText(int minute)
        {
            if (minute < 0) return "";
            int day = minute / 1440, rest = minute % 1440;
            int mo = 12; while (mo > 1 && MonthStart[mo] > day) mo--;
            return $"{day - MonthStart[mo] + 1:00}/{mo:00} {rest / 60:00}:{rest % 60:00}";
        }

        private static readonly Regex TimeInput = new Regex(@"^\s*(?:(?<d>\d{1,2})[/.\-](?<mo>\d{1,2})(?:[/.\-]\d{2,4})?\s+)?(?<h>\d{1,2})\s*[h:H]\s*(?<mi>\d{1,2})?(?:[:]\d{1,2})?\s*$");

        /// <summary>
        /// Heure saisie par le technicien (« 9h07 », « 09:07 », « 05/11 9:07 ») → minute du relevé. Sans date, on prend
        /// le dernier jour du relevé qui contient des lignes à cette heure-là (sinon le dernier jour). -1 si illisible.
        /// </summary>
        public int ParseIncidentTime(string input)
        {
            var m = TimeInput.Match(input ?? "");
            if (!m.Success) return -1;
            int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int mi = m.Groups["mi"].Success && m.Groups["mi"].Value.Length > 0 ? int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture) : 0;
            if (h > 23 || mi > 59) return -1;
            if (m.Groups["d"].Success)
            {
                int d = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), mo = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture);
                if (mo < 1 || mo > 12 || d < 1 || d > 31) return -1;
                return ((MonthStart[mo] + d - 1) * 24 + h) * 60 + mi;
            }
            var days = Entries.Where(e => e.Minute >= 0).Select(e => e.Minute / 1440).Distinct().OrderByDescending(x => x).ToList();
            if (days.Count == 0) return -1;
            foreach (var day in days)
            {
                int t = day * 1440 + h * 60 + mi;
                if (Entries.Any(e => e.Minute >= t - 2 && e.Minute <= t + 2)) return t;
            }
            return days[0] * 1440 + h * 60 + mi;
        }

        // ------------------------------------------------------------------ Filtrage

        /// <summary>Lignes qui passent le filtre (dans l'ordre chronologique). Lève ArgumentException si la regex est invalide.</summary>
        public List<FileLogEntry> Filter(ArchiveFilter f, CancellationToken ct = default(CancellationToken))
        {
            if (f == null || f.IsEmpty) return Entries;
            Regex rx = null;
            string text = f.Text ?? "";
            if (f.Regex && text.Length > 0) rx = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string tag = (f.Tag ?? "").Trim(), pid = (f.Pid ?? "").Trim();

            return Entries.AsParallel().AsOrdered().WithCancellation(ct).Where(e =>
            {
                if (f.Sources != null && !f.Sources.Contains(e.SourceId)) return false;
                if (f.Levels != null && !f.Levels.Contains(e.Level ?? "")) return false;
                if (f.FromMinute != int.MinValue && (e.Minute < f.FromMinute || e.Minute > f.ToMinute)) return false;
                if (pid.Length > 0 && e.PID != pid) return false;
                if (tag.Length > 0 && (e.Tag ?? "").IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) return false;
                if (text.Length > 0)
                {
                    if (rx != null) return rx.IsMatch(e.Message ?? "") || rx.IsMatch(e.Tag ?? "");
                    return (e.Message ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || (e.Tag ?? "").IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return true;
            }).ToList();
        }

        // ------------------------------------------------------------------ Frise

        /// <summary>Une colonne de la frise : nombre de lignes et d'erreurs sur une tranche de temps.</summary>
        public sealed class TimelineBucket
        {
            public int From, To, Lines, Errors, Incidents;
        }

        /// <summary>
        /// Frise de <paramref name="count"/> tranches sur la période du relevé. Les quelques lignes datées d'avant la mise
        /// à l'heure du terminal (01/01, 02/03…) sont écartées pour ne pas écraser la frise.
        /// </summary>
        public List<TimelineBucket> Timeline(IList<FileLogEntry> source, int count = 160)
        {
            var result = new List<TimelineBucket>();
            var minutes = source.Where(e => e.Minute >= 0).Select(e => e.Minute).ToList();
            if (minutes.Count == 0) return result;
            var sorted = minutes.OrderBy(x => x).ToList();
            int lo = sorted[(int)(sorted.Count * 0.002)], hi = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.9995))];
            if (hi <= lo) hi = lo + 1;
            double step = Math.Max(1.0, (hi - lo + 1) / (double)count);
            int n = (int)Math.Ceiling((hi - lo + 1) / step);
            for (int i = 0; i < n; i++) result.Add(new TimelineBucket { From = lo + (int)(i * step), To = lo + (int)((i + 1) * step) - 1 });
            foreach (var e in source)
            {
                if (e.Minute < lo || e.Minute > hi) continue;
                int b = Math.Min(n - 1, (int)((e.Minute - lo) / step));
                var t = result[b];
                t.Lines++;
                if (e.Level == "E" || e.Level == "F") t.Errors++;
                if (e.Tag == IncidentTag) t.Incidents++;
            }
            return result;
        }

        /// <summary>Tag des incidents signalés depuis le terminal (mode enregistreur de l'appli de diagnostic).</summary>
        public const string IncidentTag = "ADBTools-Incident";
    }

    /// <summary>Une ligne de la comparaison de deux relevés : un type de message d'erreur / avertissement.</summary>
    public sealed class LogCompareRow
    {
        public string Level { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Template { get; set; } = "";
        public int CountA { get; set; }
        public int CountB { get; set; }
        public double RateA { get; set; }
        public double RateB { get; set; }
        public string Verdict { get; set; } = "";
        public string Example { get; set; } = "";
        public string RateAText => RateA.ToString("0.#", CultureInfo.CurrentCulture);
        public string RateBText => RateB.ToString("0.#", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Comparaison de deux relevés (ex. terminal avec et sans correctif) : pour chaque type de message en erreur ou
    /// en avertissement (chiffres et adresses neutralisés), sa fréquence par heure de chaque côté. Les messages
    /// propres à un relevé, ou nettement plus fréquents, sortent en premier.
    /// </summary>
    public static class LogCompare
    {
        private static readonly Regex Hex = new Regex(@"0x[0-9a-fA-F]+|\b[0-9a-fA-F]{8,}\b", RegexOptions.Compiled);
        private static readonly Regex Num = new Regex(@"\d+", RegexOptions.Compiled);

        public static string Normalize(string msg)
        {
            string s = Num.Replace(Hex.Replace(msg ?? "", "#"), "#").Trim();
            return s.Length > 160 ? s.Substring(0, 160) : s;
        }

        public static double Hours(IList<FileLogEntry> entries)
        {
            var m = entries.Where(e => e.Minute >= 0).Select(e => e.Minute).ToList();
            if (m.Count == 0) return 1;
            m.Sort();
            int lo = m[(int)(m.Count * 0.002)], hi = m[Math.Min(m.Count - 1, (int)(m.Count * 0.9995))];
            return Math.Max(1.0 / 60, (hi - lo + 1) / 60.0);
        }

        public static List<LogCompareRow> Compare(IList<FileLogEntry> a, IList<FileLogEntry> b, int minCount = 3)
        {
            double ha = Hours(a), hb = Hours(b);
            var rows = new Dictionary<string, LogCompareRow>(StringComparer.Ordinal);
            void Count(IList<FileLogEntry> list, bool left)
            {
                foreach (var e in list)
                {
                    if (e.Level != "E" && e.Level != "F" && e.Level != "W") continue;
                    string key = e.Level + "|" + e.Tag + "|" + Normalize(e.Message);
                    if (!rows.TryGetValue(key, out var r))
                        rows[key] = r = new LogCompareRow { Level = e.Level, Tag = e.Tag ?? "", Template = Normalize(e.Message), Example = e.Time + " " + (e.Message ?? "") };
                    if (left) r.CountA++; else r.CountB++;
                }
            }
            Count(a, true); Count(b, false);
            var result = new List<LogCompareRow>();
            foreach (var r in rows.Values)
            {
                if (r.CountA + r.CountB < minCount) continue;
                r.RateA = r.CountA / ha; r.RateB = r.CountB / hb;
                if (r.CountA == 0) r.Verdict = Loc.T("Seulement dans B");
                else if (r.CountB == 0) r.Verdict = Loc.T("Seulement dans A");
                else if (r.RateB >= r.RateA * 3) r.Verdict = Loc.T("Bien plus fréquent dans B");
                else if (r.RateA >= r.RateB * 3) r.Verdict = Loc.T("Bien plus fréquent dans A");
                else continue; // fréquence comparable : sans intérêt pour la comparaison
                result.Add(r);
            }
            return result.OrderBy(r => r.Level == "E" || r.Level == "F" ? 0 : 1)
                         .ThenByDescending(r => Math.Abs(Math.Log((r.RateB + 0.05) / (r.RateA + 0.05))))
                         .ThenByDescending(r => r.CountA + r.CountB).ToList();
        }
    }
}
