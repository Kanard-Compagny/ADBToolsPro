using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Outils d'enquête de la page « Analyse de fichiers » : couloirs de la frise, « avant l'incident », tendance des
    /// constats, vie d'une application, redémarrages et sessions, résumés ANR / tombstone, pannes connues de l'équipe
    /// (signatures) et signets du technicien.
    /// </summary>
    public static class LogInsights
    {
        private const RegexOptions OI = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // ================================================================== Couloirs de la frise

        public const int LaneCount = 6;
        public static string[] LaneNames => new[]
        {
            Loc.T("Redémarrages"), Loc.T("Plantages / ANR"), Loc.T("Wi-Fi coupé"), Loc.T("Mémoire"), Loc.T("Batterie / chaleur"), Loc.T("Lecteur"),
        };

        private static readonly Regex ShutdownMsg = new Regex(@"Rebooting|Shutting down|low-level shutdown|reboot reason|Shutdown thread|shutdown reason", OI);
        private static readonly Regex ScannerFault = new Regex(@"More than \d+ Null frames? received|trigger source failed", OI);
        private static readonly Regex LmkMsg = new Regex(@"^Kill '|^Killing '|kill '|low ?memory", OI);
        private static readonly Regex HeatMsg = new Regex(@"overheat|too hot|thermal (?:shutdown|throttl|mitigation)|battery (?:is )?low|batterie faible|battery_low", OI);

        /// <summary>Couloir d'une ligne (0 à LaneCount-1), ou -1 si elle n'en relève pas.</summary>
        public static int LaneOf(LogEntry e)
        {
            string tag = e.Tag ?? "", msg = e.Message ?? "", lvl = e.Level ?? "";
            if (IsBoot(e) || (tag == "ShutdownThread" && ShutdownMsg.IsMatch(msg))) return 0;
            if ((tag == "AndroidRuntime" && msg.StartsWith("FATAL EXCEPTION", StringComparison.Ordinal)) || tag == "am_crash" || tag == "am_anr"
                || (tag == "ActivityManager" && msg.StartsWith("ANR in", StringComparison.Ordinal)) || (tag == "libc" && lvl == "F" && msg.Contains("Fatal signal")))
                return 1;
            if ((tag.StartsWith("wpa_supplicant", StringComparison.Ordinal) && msg.Contains("CTRL-EVENT-DISCONNECTED")) || (tag == "ADBTools-Wifi" && lvl == "W")) return 2;
            if (tag == "lowmemorykiller" || tag == "lmkd" && LmkMsg.IsMatch(msg) || tag == "am_low_memory"
                || (tag == "am_kill" && msg.IndexOf("lmk", StringComparison.OrdinalIgnoreCase) >= 0)
                || (tag == "ActivityManager" && msg.StartsWith("Low on memory", StringComparison.Ordinal))) return 3;
            if ((lvl == "W" || lvl == "E" || tag.StartsWith("ADBTools", StringComparison.Ordinal)) && HeatMsg.IsMatch(msg)) return 4;
            if ((msg.IndexOf("Null frame", StringComparison.Ordinal) >= 0 || msg.IndexOf("trigger source failed", StringComparison.Ordinal) >= 0) && ScannerFault.IsMatch(msg)) return 5;
            return -1;
        }

        public static bool IsBoot(LogEntry e)
        {
            string tag = e.Tag ?? "", msg = e.Message ?? "";
            return tag == "boot_progress_start" || (tag == "SystemServer" && msg.StartsWith("Entered the Android system server", StringComparison.Ordinal));
        }

        // ================================================================== Avant l'incident

        public sealed class BeforeRow
        {
            public string Kind { get; set; } = "";     // new / spike / event
            public string KindText { get; set; } = "";
            public string Time { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Text { get; set; } = "";
            public int Count { get; set; }
            public string Detail { get; set; } = "";
            public FileLogEntry Entry { get; set; }
            public string Icon => Kind == "new" ? "🆕" : Kind == "spike" ? "📈" : "⚡";
            public string ShortTime => LogFinding.ShortTime(Time).Length > 6 ? LogFinding.ShortTime(Time).Substring(6) : LogFinding.ShortTime(Time);
        }

        /// <summary>
        /// Ce qui change dans les <paramref name="windowMin"/> minutes avant <paramref name="minute"/> par rapport au reste du
        /// relevé qui précède : messages d'erreur / d'avertissement jamais vus avant, messages en forte hausse, et
        /// événements marquants (redémarrage, plantage, coupure Wi-Fi, mémoire, lecteur, incident).
        /// </summary>
        public static List<BeforeRow> BeforeIncident(LogArchive a, int minute, int windowMin = 10, HashSet<int> sources = null)
        {
            var rows = new List<BeforeRow>();
            int from = minute - windowMin, to = minute;
            int baseFrom = Math.Max(a.SpanFrom, from - 24 * 60);
            int baseMinutes = Math.Max(0, from - baseFrom);
            var baseCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var win = new Dictionary<string, (int Count, FileLogEntry First)>(StringComparer.Ordinal);
            var events = new List<BeforeRow>();
            foreach (var e in a.Entries)
            {
                if (e.Minute < baseFrom || e.Minute > to) continue;
                if (sources != null && !sources.Contains(e.SourceId)) continue;
                bool inWin = e.Minute >= from;
                if (inWin)
                {
                    int lane = LaneOf(e);
                    if (lane >= 0 || e.Tag == LogArchive.IncidentTag)
                        events.Add(new BeforeRow { Kind = "event", KindText = e.Tag == LogArchive.IncidentTag ? Loc.T("Incident signalé") : LaneNames[lane], Time = e.Time, Tag = e.Tag, Text = e.Message, Entry = e, Count = 1 });
                }
                if (e.Level != "E" && e.Level != "F" && e.Level != "W") continue;
                string key = e.Tag + "|" + LogCompare.Normalize(e.Message);
                if (inWin)
                {
                    win.TryGetValue(key, out var w);
                    win[key] = (w.Count + 1, w.First ?? e);
                }
                else { baseCount.TryGetValue(key, out int n); baseCount[key] = n + 1; }
            }

            // Événements marquants (les répétitions rapprochées du même type sont regroupées).
            foreach (var g in events.GroupBy(r => r.KindText + "|" + r.Tag + "|" + LogCompare.Normalize(r.Text)))
            {
                var first = g.First();
                first.Count = g.Count();
                rows.Add(first);
            }

            if (baseMinutes >= 30)
            {
                foreach (var kv in win)
                {
                    baseCount.TryGetValue(kv.Key, out int b);
                    var e = kv.Value.First;
                    if (b == 0)
                        rows.Add(new BeforeRow { Kind = "new", KindText = Loc.T("Jamais vu avant"), Time = e.Time, Tag = e.Tag, Text = e.Message, Count = kv.Value.Count, Entry = e,
                            Detail = Loc.F("{0} fois en {1} min, jamais dans les {2} h précédentes", kv.Value.Count, windowMin, (baseMinutes / 60.0).ToString("0.#", CultureInfo.CurrentCulture)) });
                    else
                    {
                        double rw = kv.Value.Count / (double)Math.Max(1, windowMin), rb = b / (double)baseMinutes;
                        if (kv.Value.Count >= 5 && rw >= rb * 5)
                            rows.Add(new BeforeRow { Kind = "spike", KindText = Loc.F("En forte hausse (×{0})", (rw / rb).ToString("0", CultureInfo.CurrentCulture)), Time = e.Time, Tag = e.Tag, Text = e.Message, Count = kv.Value.Count, Entry = e,
                                Detail = Loc.F("{0}/min contre {1}/min d'habitude", rw.ToString("0.#", CultureInfo.CurrentCulture), rb.ToString("0.##", CultureInfo.CurrentCulture)) });
                    }
                }
            }
            return rows.OrderBy(r => r.Kind == "event" ? 0 : r.Kind == "new" ? 1 : 2)
                       .ThenBy(r => r.Kind == "event" ? r.Entry.Index : -r.Count)
                       .Take(80).ToList();
        }

        // ================================================================== Tendance des constats

        /// <summary>Occurrences des messages d'erreur / d'avertissement dans le temps (même découpage pour tous).</summary>
        public sealed class TrendIndex
        {
            public int From, To, Buckets;
            internal readonly Dictionary<string, int[]> Series = new Dictionary<string, int[]>(StringComparer.Ordinal);
            public int[] Of(LogEntry e) => e == null ? null : Series.TryGetValue(Key(e), out var s) ? s : null;
            internal static string Key(LogEntry e) => (e.Tag ?? "") + "|" + LogCompare.Normalize(e.Message);
        }

        public static TrendIndex BuildTrends(LogArchive a, int buckets = 48)
        {
            var t = new TrendIndex { From = a.SpanFrom, To = a.SpanTo, Buckets = buckets };
            if (t.From < 0 || t.To <= t.From) return t;
            double step = (t.To - t.From + 1) / (double)buckets;
            foreach (var e in a.Entries)
            {
                if (e.Minute < t.From || e.Minute > t.To) continue;
                if (e.Level != "E" && e.Level != "F" && e.Level != "W" && e.Tag != LogArchive.IncidentTag) continue;
                string k = TrendIndex.Key(e);
                if (!t.Series.TryGetValue(k, out var s)) t.Series[k] = s = new int[buckets];
                s[Math.Min(buckets - 1, (int)((e.Minute - t.From) / step))]++;
            }
            return t;
        }

        /// <summary>Ajoute à chaque constat sa mini-courbe et une phrase : ponctuel, régulier, en hausse, en baisse.</summary>
        public static void ApplyTrends(IEnumerable<LogFinding> findings, TrendIndex index)
        {
            if (index == null || index.Buckets == 0) return;
            double step = (index.To - index.From + 1) / (double)index.Buckets;
            foreach (var f in findings)
            {
                var s = index.Of(f.FirstEntry);
                if (s == null || s.Sum() < 2) continue;
                int max = s.Max();
                var sb = new StringBuilder();
                const double w = 140, h = 18;
                for (int i = 0; i < s.Length; i++)
                {
                    double x = i * w / (s.Length - 1), y = h - (max == 0 ? 0 : s[i] * h / max);
                    sb.Append(i == 0 ? "M" : " L").Append(x.ToString("0.#", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("0.#", CultureInfo.InvariantCulture));
                }
                f.TrendPath = sb.ToString();
                int firstIdx = Array.FindIndex(s, v => v > 0), active = s.Count(v => v > 0);
                string since = LogArchive.MinuteText(index.From + (int)(firstIdx * step));
                int third = Math.Max(1, s.Length / 3);
                double early = s.Take(third).Sum(), late = s.Skip(s.Length - third).Sum();
                string trend;
                if (active <= 2) trend = Loc.F("Ponctuel ({0})", since);
                else if (late > early * 1.5 + 1) trend = Loc.F("↗ En hausse — depuis le {0}", since);
                else if (early > late * 1.5 + 1) trend = Loc.F("↘ En baisse — depuis le {0}", since);
                else trend = Loc.F("Régulier — depuis le {0}", since);
                f.TrendText = trend;
            }
        }

        // ================================================================== Vie d'une application

        public sealed class AppEvent
        {
            public string Time { get; set; } = "";
            public string Kind { get; set; } = "";       // start, died, killed_lmk, killed, crash, anr, foreground
            public string Text { get; set; } = "";
            public string Detail { get; set; } = "";
            public FileLogEntry Entry { get; set; }
            public string Icon
            {
                get
                {
                    switch (Kind)
                    {
                        case "start": return "▶";
                        case "crash": return "💥";
                        case "anr": return "🧊";
                        case "killed_lmk": return "🧠";
                        case "killed": return "✖";
                        case "died": return "⏹";
                        default: return "•";
                    }
                }
            }
            public string ShortTime => LogFinding.ShortTime(Time);
        }

        public sealed class AppLife
        {
            public string Package = "";
            public List<AppEvent> Events = new List<AppEvent>();
            public int Starts, Crashes, Anrs, MemoryKills, OtherKills, Deaths;
            public string Summary => Loc.F("{0} démarrage(s) · {1} plantage(s) · {2} ANR · {3} arrêt(s) par manque de mémoire · {4} autre(s) arrêt(s)",
                Starts, Crashes, Anrs, MemoryKills, OtherKills + Math.Max(0, Deaths - Crashes - MemoryKills - OtherKills));
        }

        private static readonly Regex StartProc = new Regex(@"^Start proc (\d+):([\w.]+)", RegexOptions.Compiled);
        private static readonly Regex ProcDied = new Regex(@"^Process ([\w.:]+) \(pid (\d+)\) has died", RegexOptions.Compiled);
        private static readonly Regex Killing = new Regex(@"^Killing (\d+):([\w.:]+)/\S+ \(adj (-?\d+)\): (.*)$", RegexOptions.Compiled);
        private static readonly Regex LmkKill = new Regex(@"Kill(?:ing)? '([\w.:]+)' \((\d+)\)", RegexOptions.Compiled);
        private static readonly Regex AnrIn = new Regex(@"^ANR in ([\w.:]+)", RegexOptions.Compiled);
        private static readonly Regex CrashProcess = new Regex(@"^Process: ([\w.:]+), PID: (\d+)", RegexOptions.Compiled);

        private static string[] EventFields(string msg)
        {
            msg = (msg ?? "").Trim();
            if (msg.StartsWith("[", StringComparison.Ordinal)) msg = msg.Substring(1);
            if (msg.EndsWith("]", StringComparison.Ordinal)) msg = msg.Substring(0, msg.Length - 1);
            return msg.Split(',');
        }

        private static bool SamePkg(string proc, string pkg) => proc == pkg || proc.StartsWith(pkg + ":", StringComparison.Ordinal);

        /// <summary>Applications démarrées dans le relevé (les plus fréquentes d'abord), pour choisir l'appli métier.</summary>
        public static List<KeyValuePair<string, int>> StartedApps(LogArchive a)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in a.Entries)
            {
                string pkg = null;
                if (e.Tag == "am_proc_start") { var f = EventFields(e.Message); if (f.Length >= 4) pkg = f[3].Trim(); }
                else if (e.Tag == "ActivityManager") { var m = StartProc.Match(e.Message ?? ""); if (m.Success) pkg = m.Groups[2].Value; }
                if (string.IsNullOrEmpty(pkg)) continue;
                int c = pkg.IndexOf(':'); if (c > 0) pkg = pkg.Substring(0, c);
                counts.TryGetValue(pkg, out int n); counts[pkg] = n + 1;
            }
            return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        }

        /// <summary>Démarrages, arrêts et leur cause pour une application (journaux main, system, events, crash).</summary>
        public static AppLife Lifecycle(LogArchive a, string pkg)
        {
            var life = new AppLife { Package = pkg ?? "" };
            if (string.IsNullOrEmpty(pkg)) return life;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void AddEvent(FileLogEntry e, string kind, string text, string detail, string pid)
            {
                // Le même événement est souvent écrit dans deux journaux (main et events) : une seule fois.
                string key = kind + "|" + pid + "|" + (e.Minute >= 0 ? e.Minute.ToString(CultureInfo.InvariantCulture) : e.Time);
                if (!string.IsNullOrEmpty(pid) && !seen.Add(key)) return;
                life.Events.Add(new AppEvent { Time = e.Time, Kind = kind, Text = text, Detail = detail ?? "", Entry = e });
                switch (kind)
                {
                    case "start": life.Starts++; break;
                    case "crash": life.Crashes++; break;
                    case "anr": life.Anrs++; break;
                    case "killed_lmk": life.MemoryKills++; break;
                    case "killed": life.OtherKills++; break;
                    case "died": life.Deaths++; break;
                }
            }
            foreach (var e in a.Entries)
            {
                string tag = e.Tag ?? "", msg = e.Message ?? "";
                Match m;
                switch (tag)
                {
                    case "am_proc_start":
                    {
                        var f = EventFields(msg);
                        if (f.Length >= 4 && SamePkg(f[3].Trim(), pkg)) AddEvent(e, "start", Loc.T("Démarrage"), f.Length >= 5 ? f[4].Trim() : "", f[1].Trim());
                        break;
                    }
                    case "am_proc_died":
                    {
                        var f = EventFields(msg);
                        if (f.Length >= 3 && SamePkg(f[2].Trim(), pkg)) AddEvent(e, "died", Loc.T("Processus terminé"), "", f[1].Trim());
                        break;
                    }
                    case "am_kill":
                    {
                        var f = EventFields(msg);
                        if (f.Length >= 5 && SamePkg(f[2].Trim(), pkg))
                        {
                            string reason = string.Join(",", f.Skip(4)).Trim();
                            bool lmk = reason.IndexOf("lmk", StringComparison.OrdinalIgnoreCase) >= 0 || reason.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0;
                            AddEvent(e, lmk ? "killed_lmk" : "killed", lmk ? Loc.T("Arrêté par manque de mémoire") : Loc.T("Arrêté par le système"), reason, f[1].Trim());
                        }
                        break;
                    }
                    case "am_anr":
                    {
                        var f = EventFields(msg);
                        if (f.Length >= 3 && SamePkg(f[2].Trim(), pkg)) AddEvent(e, "anr", Loc.T("Application figée (ANR)"), f.Length >= 5 ? string.Join(",", f.Skip(4)).Trim() : "", f[1].Trim());
                        break;
                    }
                    case "am_crash":
                    {
                        var f = EventFields(msg);
                        if (f.Length >= 3 && SamePkg(f[2].Trim(), pkg)) AddEvent(e, "crash", Loc.T("Plantage"), f.Length >= 6 ? (f[4].Trim() + ": " + f[5].Trim()) : "", f[1].Trim());
                        break;
                    }
                    case "ActivityManager":
                        if ((m = StartProc.Match(msg)).Success && SamePkg(m.Groups[2].Value, pkg)) AddEvent(e, "start", Loc.T("Démarrage"), msg, m.Groups[1].Value);
                        else if ((m = ProcDied.Match(msg)).Success && SamePkg(m.Groups[1].Value, pkg)) AddEvent(e, "died", Loc.T("Processus terminé"), msg, m.Groups[2].Value);
                        else if ((m = Killing.Match(msg)).Success && SamePkg(m.Groups[2].Value, pkg))
                        {
                            bool lmk = m.Groups[4].Value.IndexOf("lmk", StringComparison.OrdinalIgnoreCase) >= 0 || m.Groups[4].Value.IndexOf("memory", StringComparison.OrdinalIgnoreCase) >= 0;
                            AddEvent(e, lmk ? "killed_lmk" : "killed", lmk ? Loc.T("Arrêté par manque de mémoire") : Loc.T("Arrêté par le système"), m.Groups[4].Value, m.Groups[1].Value);
                        }
                        else if ((m = AnrIn.Match(msg)).Success && SamePkg(m.Groups[1].Value, pkg)) AddEvent(e, "anr", Loc.T("Application figée (ANR)"), msg, "anr" + e.Minute);
                        break;
                    case "lmkd":
                    case "lowmemorykiller":
                        if ((m = LmkKill.Match(msg)).Success && SamePkg(m.Groups[1].Value, pkg)) AddEvent(e, "killed_lmk", Loc.T("Arrêté par manque de mémoire"), msg, m.Groups[2].Value);
                        break;
                    case "AndroidRuntime":
                        if ((m = CrashProcess.Match(msg)).Success && SamePkg(m.Groups[1].Value, pkg)) AddEvent(e, "crash", Loc.T("Plantage"), msg, m.Groups[2].Value);
                        break;
                }
            }
            return life;
        }

        // ================================================================== Redémarrages et sessions

        public sealed class Session
        {
            public int Number { get; set; }
            public string Start { get; set; } = "";
            public string End { get; set; } = "";
            public string Duration { get; set; } = "";
            public string EndCause { get; set; } = "";
            public bool Unexpected { get; set; }
            public bool FromBoot { get; set; }
            /// <summary>Raison du démarrage qui ouvre la session (bootstat / historique), vide si inconnue.</summary>
            public string StartReason { get; set; } = "";
            public FileLogEntry StartEntry { get; set; }
            public string Icon => Unexpected ? "⚠️" : "🔄";
        }

        private static readonly Regex ReasonRx = new Regex(@"reason[:=\s]+'?([\w\-,. ]{2,40})", OI);

        /// <summary>Sessions entre deux démarrages du terminal, et comment chacune s'est terminée.</summary>
        private static readonly Regex BootstatReason = new Regex(@"^(?:Canonical boot reason|Normalized last reboot reason)\s*:\s*(\S+)", RegexOptions.Compiled);

        /// <summary>Raison Android d'un démarrage (bootstat / persist.sys.boot.reason) en clair, et si elle est anormale.</summary>
        public static (string Text, bool Unexpected) DescribeBootReason(string reason)
        {
            string r = (reason ?? "").ToLowerInvariant();
            if (r.Length == 0) return (Loc.T("Redémarrage inattendu (aucun arrêt propre noté)"), true);
            bool user = r.Contains("userrequested") || r.Contains("user_requested");
            if (r.Contains("panic")) return (Loc.T("Plantage du noyau (kernel panic)"), true);
            if (r.Contains("watchdog") || r.Contains("wdog")) return (Loc.T("Blocage du système (watchdog)"), true);
            if (r.Contains("thermal")) return (Loc.T("Arrêt thermique (surchauffe)"), true);
            if (r.Contains("battery") || r.Contains("lowbatt")) return (Loc.T("Batterie à plat"), true);
            if (r.Contains("longkey") || r.Contains("long_key") || r.Contains("longpress")) return (Loc.T("Redémarrage forcé (appui long sur le bouton marche)"), true);
            if (r.Contains("ota")) return (Loc.T("Redémarrage pour mise à jour du système (OTA)"), false);
            if (r.Contains("recovery") || r.Contains("bootloader")) return (Loc.T("Redémarrage en mode recovery / bootloader"), false);
            if (r.Contains("cold") || r.Contains("hard") || r.Contains("hw_reset") || r.Contains("pmic") || r.Contains("abnormal") || r.Contains("crash") || r.Contains("unknown"))
                return (Loc.F("Redémarrage anormal ({0})", reason), true);
            if (r.StartsWith("shutdown", StringComparison.Ordinal)) return (user ? Loc.T("Arrêt demandé par l'utilisateur") : Loc.T("Arrêt demandé"), false);
            if (r.StartsWith("reboot", StringComparison.Ordinal))
                return (user ? Loc.T("Redémarrage demandé par l'utilisateur") : r.Contains("shell") || r.Contains("adb") ? Loc.T("Redémarrage demandé par commande") : Loc.T("Redémarrage demandé"), false);
            return (reason, false);
        }

        /// <summary>Sessions entre deux démarrages du terminal, et comment chacune s'est terminée.</summary>
        public static List<Session> Sessions(LogArchive a)
        {
            var list = new List<Session>();
            if (a.Entries.Count == 0 || a.SpanFrom < 0) return list;
            int lo = a.SpanFrom - 60, hi = a.SpanTo + 60;
            var boots = new List<int>();
            var shutdowns = new List<(int Index, string Reason)>();
            var bootstat = new List<(int Minute, string Reason)>();
            for (int i = 0; i < a.Entries.Count; i++)
            {
                var e = a.Entries[i];
                if (IsBoot(e))
                {
                    // Lignes d'avant la mise à l'heure du terminal : sans date fiable, ignorées.
                    if (e.Minute < lo || e.Minute > hi) continue;
                    // Un même démarrage est noté par plusieurs marqueurs à quelques secondes d'écart.
                    if (boots.Count > 0 && Math.Abs(e.Minute - a.Entries[boots[boots.Count - 1]].Minute) <= 3) continue;
                    boots.Add(i);
                }
                else if (e.Tag == "ShutdownThread" && ShutdownMsg.IsMatch(e.Message ?? ""))
                {
                    var m = ReasonRx.Match(e.Message ?? "");
                    string kind = (e.Message ?? "").IndexOf("reboot", StringComparison.OrdinalIgnoreCase) >= 0 ? "reboot" : "shutdown";
                    shutdowns.Add((i, kind + (m.Success ? "," + m.Groups[1].Value.Trim() : "")));
                }
                else if (e.Tag == "bootstat" && e.Minute >= lo)
                {
                    var m = BootstatReason.Match(e.Message ?? "");
                    if (m.Success) bootstat.Add((e.Minute, m.Groups[1].Value.Trim().TrimEnd('.')));
                }
            }
            string ReasonFor(int bootMinute)
            {
                var bs = bootstat.Where(x => x.Minute >= bootMinute - 1 && x.Minute <= bootMinute + 4).Select(x => x.Reason).FirstOrDefault();
                if (!string.IsNullOrEmpty(bs)) return bs;
                return a.BootReasons.Where(x => Math.Abs(x.Minute - bootMinute) <= 4).OrderBy(x => Math.Abs(x.Minute - bootMinute)).Select(x => x.Reason).FirstOrDefault();
            }

            // Bornes : début du relevé (s'il ne commence pas déjà par un démarrage), puis chaque démarrage.
            var starts = new List<int>();
            bool startsWithBoot = boots.Count > 0 && a.Entries[boots[0]].Minute - a.SpanFrom <= 3;
            if (!startsWithBoot) starts.Add(0);
            starts.AddRange(boots);
            for (int s = 0; s < starts.Count; s++)
            {
                int a0 = starts[s], a1 = s + 1 < starts.Count ? starts[s + 1] - 1 : a.Entries.Count - 1;
                var first = FirstValid(a, a0, a1, true);
                var last = FirstValid(a, a0, a1, false);
                if (first == null || last == null) continue;
                bool fromBoot = boots.Contains(a0);
                var sess = new Session
                {
                    Number = list.Count + 1, StartEntry = first, FromBoot = fromBoot,
                    Start = LogArchive.MinuteText(first.Minute) + (fromBoot ? "" : " " + Loc.T("(début du relevé)")),
                    End = LogArchive.MinuteText(last.Minute),
                    Duration = DurationText(last.Minute - first.Minute),
                };
                if (fromBoot)
                {
                    string why = ReasonFor(a.Entries[a0].Minute);
                    if (!string.IsNullOrEmpty(why)) sess.StartReason = DescribeBootReason(why).Text + " — " + why;
                }
                if (s + 1 >= starts.Count) sess.EndCause = Loc.T("Fin du relevé");
                else
                {
                    string why = ReasonFor(a.Entries[starts[s + 1]].Minute);
                    var shut = shutdowns.LastOrDefault(x => x.Index >= a0 && x.Index <= a1);
                    if (string.IsNullOrEmpty(why) && shut.Index > 0) why = shut.Reason;
                    var d = DescribeBootReason(why);
                    sess.EndCause = d.Text + (string.IsNullOrEmpty(why) ? "" : " — " + Loc.F("raison Android : {0}", why));
                    sess.Unexpected = d.Unexpected;
                }
                list.Add(sess);
            }
            return list;
        }

        /// <summary>
        /// Les premières minutes après un démarrage, le lecteur et le réseau écrivent beaucoup d'erreurs passagères
        /// (services qui démarrent, réseau pas encore connecté) : regroupées en un seul constat d'information par démarrage.
        /// </summary>
        public static List<LogFinding> FoldBootNoise(List<LogFinding> findings, IList<Session> sessions, int minutesAfterBoot = 4)
        {
            if (sessions == null) return findings;
            var bootMinutes = sessions.Where(s => s.FromBoot && s.StartEntry != null).Select(s => s.StartEntry.Minute).ToList();
            if (bootMinutes.Count == 0) return findings;
            var result = new List<LogFinding>();
            var folded = new Dictionary<int, List<LogFinding>>();
            foreach (var f in findings)
            {
                bool candidate = f.Category == LogAnalyzer.CatScanner || f.Category == LogAnalyzer.CatNetwork;
                int first = f.FirstEntry is FileLogEntry fe ? fe.Minute : LogArchive.ToMinute(f.FirstTime);
                int last = string.IsNullOrEmpty(f.LastTime) ? first : LogArchive.ToMinute(f.LastTime);
                int boot = candidate && first >= 0 ? bootMinutes.Where(b => first >= b && Math.Max(first, last) <= b + minutesAfterBoot).DefaultIfEmpty(-1).First() : -1;
                if (boot < 0) { result.Add(f); continue; }
                if (!folded.TryGetValue(boot, out var l)) folded[boot] = l = new List<LogFinding>();
                l.Add(f);
            }
            foreach (var kv in folded)
            {
                var l = kv.Value;
                if (l.Count == 1) { result.Add(l[0]); continue; }
                var firstF = l.OrderBy(x => x.FirstEntry is FileLogEntry fe ? fe.Index : int.MaxValue).First();
                result.Add(new LogFinding
                {
                    Category = LogAnalyzer.CatOverview, Severity = FindingSeverity.Info,
                    Title = Loc.T("Messages au démarrage du terminal (normaux après un redémarrage)"),
                    App = string.Join(", ", l.Select(x => x.App).Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(3)),
                    Detail = Loc.F("{0} messages différents ({1} fois) du lecteur ou du réseau dans les {2} minutes qui suivent le démarrage de {3}, pendant que les services se lancent.",
                        l.Count, l.Sum(x => Math.Max(1, x.Count)), minutesAfterBoot, LogArchive.MinuteText(kv.Key)),
                    Advice = Loc.T("Sans conséquence s'ils ne se répètent pas ensuite. À regarder seulement si le lecteur ou le réseau ne fonctionne pas après le démarrage."),
                    Count = l.Sum(x => Math.Max(1, x.Count)), FirstEntry = firstF.FirstEntry, FirstTime = firstF.FirstTime, LastTime = l.Select(x => x.LastTime).Max() ?? "",
                });
            }
            return result;
        }

        /// <summary>Un constat par redémarrage inattendu (dans la période donnée).</summary>
        public static List<LogFinding> SessionFindings(IList<Session> sessions, int from = int.MinValue, int to = int.MaxValue)
        {
            var list = new List<LogFinding>();
            if (sessions == null) return list;
            for (int i = 0; i + 1 < sessions.Count; i++)
            {
                if (!sessions[i].Unexpected) continue;
                var next = sessions[i + 1].StartEntry;
                if (next == null || (from != int.MinValue && (next.Minute < from || next.Minute > to))) continue;
                list.Add(new LogFinding
                {
                    Category = LogAnalyzer.CatSystem, Severity = FindingSeverity.Bad, Title = Loc.T("Redémarrage inattendu du terminal"),
                    Detail = Loc.F("La session {0} ({1} → {2}) s'est terminée sans arrêt propre noté dans le journal.", sessions[i].Number, sessions[i].Start, sessions[i].End),
                    Advice = Loc.T("Plantage du système, batterie retirée ou à plat, ou appui long sur le bouton marche : regarder les dernières lignes de la session précédente et l'onglet « Sessions »."),
                    Count = 1, FirstEntry = next, FirstTime = next.Time ?? "", LastTime = "",
                });
            }
            return list;
        }

        private static FileLogEntry FirstValid(LogArchive a, int from, int to, bool forward)
        {
            int lo = a.SpanFrom - 60, hi = a.SpanTo + 60;
            if (forward) { for (int i = from; i <= to; i++) if (a.Entries[i].Minute >= lo && a.Entries[i].Minute <= hi) return a.Entries[i]; }
            else { for (int i = to; i >= from; i--) if (a.Entries[i].Minute >= lo && a.Entries[i].Minute <= hi) return a.Entries[i]; }
            return null;
        }

        public static string DurationText(int minutes)
        {
            if (minutes < 0) minutes = 0;
            return minutes >= 1440 ? Loc.F("{0} j {1} h", minutes / 1440, (minutes % 1440) / 60)
                 : minutes >= 60 ? Loc.F("{0} h {1:00}", minutes / 60, minutes % 60) : Loc.F("{0} min", minutes);
        }

        // ================================================================== ANR et tombstones

        public sealed class AnnexSummary
        {
            public string Package = "", Title = "", Text = "";
            public int Minute = -1;
        }

        private static readonly Regex AnrPid = new Regex(@"^----- pid (\d+) at (\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2})", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex CmdLine = new Regex(@"^Cmd line: (\S+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex Subject = new Regex(@"^(?:Subject|Reason): (.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TombName = new Regex(@"pid: (\d+), tid: \d+, name: .*?>>> (\S+) <<<", RegexOptions.Compiled);
        private static readonly Regex TombSignal = new Regex(@"^signal \d+ \((\w+)\).*$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TombAbort = new Regex(@"^Abort message: '(.*)'$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TombTime = new Regex(@"^Timestamp: (\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2})", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Résumé lisible d'un rapport ANR ou d'un tombstone (null si le contenu n'est pas reconnu).</summary>
        public static AnnexSummary Summarize(ArchiveFileKind kind, string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var s = new AnnexSummary();
            var sb = new StringBuilder();
            if (kind == ArchiveFileKind.Anr)
            {
                var cmd = CmdLine.Match(text);
                if (!cmd.Success) return null;
                s.Package = cmd.Groups[1].Value;
                var pid = AnrPid.Match(text);
                if (pid.Success) s.Minute = MinuteOf(pid);
                s.Title = Loc.F("Application figée (ANR) : {0}", s.Package);
                var subj = Subject.Match(text);
                if (subj.Success) sb.AppendLine(Loc.F("Raison : {0}", subj.Groups[1].Value.Trim()));
                // Fil principal : état et premières lignes de la pile.
                int mi = text.IndexOf("\"main\"", StringComparison.Ordinal);
                if (mi >= 0)
                {
                    var lines = text.Substring(mi).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                    var head = lines[0];
                    var st = Regex.Match(head, @"tid=\d+ (\w+)");
                    sb.AppendLine(Loc.F("Fil principal : {0}", st.Success ? st.Groups[1].Value : head.Trim()));
                    int frames = 0;
                    foreach (var l in lines.Skip(1))
                    {
                        string t = l.Trim();
                        if (t.Length == 0) break;
                        if (t.StartsWith("at ", StringComparison.Ordinal) || t.StartsWith("- waiting", StringComparison.Ordinal) || t.StartsWith("- locked", StringComparison.Ordinal) || t.StartsWith("native:", StringComparison.Ordinal))
                        {
                            sb.AppendLine("   " + t);
                            if (++frames >= 10) break;
                        }
                    }
                }
            }
            else if (kind == ArchiveFileKind.Tombstone)
            {
                var name = TombName.Match(text);
                if (!name.Success) return null;
                s.Package = name.Groups[2].Value;
                var tt = TombTime.Match(text);
                if (tt.Success) s.Minute = MinuteOf(tt);
                var sig = TombSignal.Match(text);
                s.Title = Loc.F("Plantage natif : {0}", s.Package) + (sig.Success ? " (" + sig.Groups[1].Value + ")" : "");
                var ab = TombAbort.Match(text);
                if (ab.Success) sb.AppendLine(Loc.F("Message : {0}", ab.Groups[1].Value));
                int frames = 0;
                foreach (var l in text.Split('\n'))
                {
                    string t = l.Trim();
                    if (Regex.IsMatch(t, @"^#\d{2} pc "))
                    {
                        sb.AppendLine("   " + t);
                        if (++frames >= 8) break;
                    }
                }
            }
            else return null;
            s.Text = sb.ToString().TrimEnd();
            return s;
        }

        private static int MinuteOf(Match m)
        {
            // Groupes : (… année)(mois)(jour)(heure)(minute) — les 5 derniers.
            int n = m.Groups.Count;
            int month = int.Parse(m.Groups[n - 4].Value, CultureInfo.InvariantCulture), day = int.Parse(m.Groups[n - 3].Value, CultureInfo.InvariantCulture);
            int hour = int.Parse(m.Groups[n - 2].Value, CultureInfo.InvariantCulture), min = int.Parse(m.Groups[n - 1].Value, CultureInfo.InvariantCulture);
            return LogArchive.ToMinute($"{month:00}-{day:00} {hour:00}:{min:00}:00.000");
        }

        /// <summary>Constats tirés des rapports ANR et tombstones du relevé (dans la période donnée).</summary>
        public static List<LogFinding> AnnexFindings(LogArchive a, int from = int.MinValue, int to = int.MaxValue)
        {
            var list = new List<LogFinding>();
            foreach (var f in a.OtherFiles.Where(x => x.Annex != null))
            {
                var s = f.Annex;
                if (s.Minute >= 0 && (s.Minute < from || s.Minute > to)) continue;
                if (s.Minute < 0 && from != int.MinValue) continue;
                var near = s.Minute >= 0 ? a.Entries.FirstOrDefault(e => e.Minute >= s.Minute) : null;
                list.Add(new LogFinding
                {
                    Category = LogAnalyzer.CatCrash, Severity = FindingSeverity.Bad, Title = s.Title, App = s.Package,
                    Detail = s.Text, Advice = Loc.F("Rapport complet : « Autres fichiers » ▸ {0}", f.Name), Count = 1,
                    FirstEntry = near, FirstTime = s.Minute >= 0 ? LogArchive.MinuteText(s.Minute) : "", LastTime = "",
                });
            }
            return list;
        }

        // ================================================================== Pannes connues (signatures de l'équipe)

        public sealed class Signature
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Pattern { get; set; } = "";
            public string Diagnosis { get; set; } = "";
            public string Solution { get; set; } = "";
            public string Severity { get; set; } = "bad";
            public string Author { get; set; } = "";
            public DateTime Created { get; set; } = DateTime.Now;
            public string Source { get; set; } = "";   // fichier d'origine (équipe / poste), non enregistré
            public string Display => Title + (string.IsNullOrEmpty(Tag) ? "" : " · " + Tag);
        }

        public static string CatKnown => Loc.T("Pannes connues (équipe)");

        /// <summary>Motif proposé pour une ligne : texte du message, chiffres et adresses remplacés par un joker.</summary>
        public static string SuggestPattern(string message)
        {
            string t = LogCompare.Normalize(message);
            return Regex.Escape(t).Replace(@"\#", "#").Replace("#", @"[0-9a-fA-Fx]+");
        }

        public static List<Signature> LoadSignatures(params string[] paths)
        {
            var all = new List<Signature>();
            foreach (var p in paths.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var list = JsonSerializer.Deserialize<List<Signature>>(File.ReadAllText(p, Encoding.UTF8));
                    if (list == null) continue;
                    foreach (var s in list) { s.Source = p; if (!all.Any(x => x.Id == s.Id)) all.Add(s); }
                }
                catch { /* fichier illisible : ignoré */ }
            }
            return all;
        }

        public static void SaveSignatures(string path, IEnumerable<Signature> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list.ToList(), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>Lignes du relevé qui correspondent aux pannes connues : un constat par panne trouvée.</summary>
        public static List<LogFinding> MatchSignatures(IList<FileLogEntry> entries, IList<Signature> sigs)
        {
            var result = new List<LogFinding>();
            if (sigs == null || sigs.Count == 0) return result;
            var compiled = new List<(Signature S, Regex R)>();
            foreach (var s in sigs)
            {
                if (string.IsNullOrWhiteSpace(s.Pattern)) continue;
                try { compiled.Add((s, new Regex(s.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))); }
                catch (ArgumentException) { }
            }
            var found = new Dictionary<string, LogFinding>();
            foreach (var e in entries)
            {
                foreach (var c in compiled)
                {
                    if (c.S.Tag.Length > 0 && !string.Equals(c.S.Tag, e.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                    bool hit;
                    try { hit = c.R.IsMatch(e.Message ?? ""); } catch (RegexMatchTimeoutException) { hit = false; }
                    if (!hit) continue;
                    if (!found.TryGetValue(c.S.Id, out var f))
                    {
                        found[c.S.Id] = f = new LogFinding
                        {
                            Category = CatKnown, Severity = c.S.Severity == "warn" ? FindingSeverity.Warn : FindingSeverity.Bad,
                            Title = "⭐ " + c.S.Title, App = e.Tag, Detail = c.S.Diagnosis, Advice = c.S.Solution, FirstEntry = e, FirstTime = e.Time ?? "",
                        };
                    }
                    f.Count++;
                    f.LastTime = e.Time ?? "";
                }
            }
            return found.Values.ToList();
        }

        // ================================================================== Signets du technicien

        public sealed class Bookmark
        {
            public string Time { get; set; } = "";
            public string Tag { get; set; } = "";
            public string Message { get; set; } = "";
            public string Note { get; set; } = "";
            public int Index { get; set; } = -1;
            [System.Text.Json.Serialization.JsonIgnore] public FileLogEntry Entry { get; set; }
            public string ShortTime => LogFinding.ShortTime(Time);
        }

        /// <summary>Fichier des signets d'un relevé (rangé dans les données d'ADB Tools, par chemin du relevé).</summary>
        public static string BookmarkFile(string dataFolder, string archivePath)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes((archivePath ?? "").ToLowerInvariant()));
                return Path.Combine(dataFolder, "signets", BitConverter.ToString(h, 0, 8).Replace("-", "") + ".json");
            }
        }

        public static List<Bookmark> LoadBookmarks(string file, LogArchive a)
        {
            var list = new List<Bookmark>();
            try
            {
                if (!File.Exists(file)) return list;
                list = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(file, Encoding.UTF8)) ?? new List<Bookmark>();
                foreach (var b in list)
                {
                    // On retrouve la ligne par sa position, sinon par son heure, son tag et son message.
                    FileLogEntry e = b.Index >= 0 && b.Index < a.Entries.Count ? a.Entries[b.Index] : null;
                    if (e == null || e.Time != b.Time || e.Tag != b.Tag)
                        e = a.Entries.FirstOrDefault(x => x.Time == b.Time && x.Tag == b.Tag && (x.Message ?? "").StartsWith(b.Message ?? "", StringComparison.Ordinal));
                    b.Entry = e;
                    if (e != null) b.Index = e.Index;
                }
                list.RemoveAll(b => b.Entry == null);
            }
            catch { list.Clear(); }
            return list.OrderBy(b => b.Index).ToList();
        }

        public static void SaveBookmarks(string file, IEnumerable<Bookmark> list)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonSerializer.Serialize(list.ToList(), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }
}
