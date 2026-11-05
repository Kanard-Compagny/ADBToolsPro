using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ADBToolsNew;

namespace ADBToolsNew.Core
{
    public enum FindingSeverity { Info = 0, Warn = 1, Bad = 2 }

    /// <summary>Un problème repéré dans les logs (regroupé : « 12 × plantage de com.exemple »).</summary>
    public sealed class LogFinding
    {
        internal string Key;
        internal int LastIndex = -1000;
        internal int CategoryOrder;

        public string Category { get; set; } = "";
        public FindingSeverity Severity { get; set; }
        public string Title { get; set; } = "";
        public string App { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Advice { get; set; } = "";
        public int Count { get; set; }
        public string FirstTime { get; set; } = "";
        public string LastTime { get; set; } = "";
        /// <summary>Première ligne concernée (pour la retrouver dans le tableau).</summary>
        public LogEntry FirstEntry { get; set; }
        /// <summary>Quelques lignes brutes d'exemple (pile d'appels d'un plantage…).</summary>
        public List<string> Sample { get; } = new List<string>();

        // --- Affichage
        public string SeverityKey => Severity == FindingSeverity.Bad ? "bad" : Severity == FindingSeverity.Warn ? "warn" : "info";
        public string SeverityIcon => Severity == FindingSeverity.Bad ? "❌" : Severity == FindingSeverity.Warn ? "⚠️" : "ℹ️";
        public string CountText => Count > 1 ? Loc.F("{0} fois", Count) : "";
        public string TimeText
        {
            get
            {
                string a = ShortTime(FirstTime), b = ShortTime(LastTime);
                if (a.Length == 0) return "";
                return a == b || b.Length == 0 ? a : a + " → " + b;
            }
        }
        public string SampleText => string.Join("\n", Sample);
        /// <summary>Concerne l'application ciblée dans la page Logs (mise en avant 🎯).</summary>
        public bool IsTarget { get; set; }
        public string DisplayTitle => IsTarget ? "🎯 " + Title : Title;

        /// <summary>
        /// Vrai si le problème vient de l'application <paramref name="package"/> (nom de package, ou l'un de ses
        /// processus « package:service ») ou de son PID.
        /// </summary>
        public bool ConcernsApp(string package, string pid)
        {
            if (!string.IsNullOrEmpty(package) && !string.IsNullOrEmpty(App)
                && (App == package || App.StartsWith(package + ":", StringComparison.Ordinal) || App.StartsWith(package + "/", StringComparison.Ordinal)))
                return true;
            return !string.IsNullOrEmpty(pid) && FirstEntry != null && FirstEntry.PID == pid
                   && Category != LogAnalyzer.CatSystem && Category != LogAnalyzer.CatOverview;
        }
        public bool HasSample => Sample.Count > 0;
        public string LinesLabel => Loc.T("Voir les lignes");
        public string AppLabel => string.IsNullOrEmpty(App) ? "" : App;

        public static string ShortTime(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            int dot = t.LastIndexOf('.');
            return dot > 0 ? t.Substring(0, dot) : t;
        }
    }

    /// <summary>Résultat d'une analyse : problèmes triés du plus grave au moins grave.</summary>
    public sealed class LogAnalysis
    {
        public List<LogFinding> Findings { get; } = new List<LogFinding>();
        public int LinesAnalyzed { get; set; }
        public string FirstTime { get; set; } = "";
        public string LastTime { get; set; } = "";
        public string Source { get; set; } = "";

        public int BadCount => Findings.Count(f => f.Severity == FindingSeverity.Bad);
        public int WarnCount => Findings.Count(f => f.Severity == FindingSeverity.Warn);
        public int InfoCount => Findings.Count(f => f.Severity == FindingSeverity.Info);

        /// <summary>Verdict en une phrase.</summary>
        public string Verdict
        {
            get
            {
                if (LinesAnalyzed == 0) return Loc.T("Aucune ligne de log à analyser.");
                if (BadCount > 0) return Loc.F("{0} problème(s) important(s) et {1} point(s) à surveiller.", BadCount, WarnCount);
                if (WarnCount > 0) return Loc.F("Aucun problème important, {0} point(s) à surveiller.", WarnCount);
                return Loc.T("Aucun problème détecté dans ces logs.");
            }
        }

        public string PeriodText
        {
            get
            {
                string a = LogFinding.ShortTime(FirstTime), b = LogFinding.ShortTime(LastTime);
                string lines = Loc.F("{0} lignes analysées", LinesAnalyzed.ToString("N0", CultureInfo.CurrentCulture));
                return a.Length == 0 ? lines : lines + " · " + a + " → " + b;
            }
        }

        /// <summary>Rapport texte (copie ou fichier .txt joint à un ticket).</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Loc.T("ANALYSE DES LOGS — ADB Tools Pro"));
            sb.AppendLine(Loc.F("Date de l'analyse : {0:dd/MM/yyyy HH:mm}", DateTime.Now));
            if (!string.IsNullOrEmpty(Source)) sb.AppendLine(Loc.F("Source : {0}", Source));
            sb.AppendLine(PeriodText);
            sb.AppendLine(Verdict);
            foreach (var group in Findings.GroupBy(f => f.Category))
            {
                sb.AppendLine();
                sb.AppendLine("=== " + group.Key.ToUpperInvariant() + " ===");
                foreach (var f in group)
                {
                    sb.Append(f.SeverityIcon).Append(' ').Append(f.Title);
                    if (!string.IsNullOrEmpty(f.App)) sb.Append(" — ").Append(f.App);
                    if (f.Count > 1) sb.Append(" (").Append(f.CountText).Append(')');
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(f.TimeText)) sb.AppendLine("   " + f.TimeText);
                    if (!string.IsNullOrEmpty(f.Detail)) sb.AppendLine("   " + f.Detail.Replace("\n", "\n   "));
                    if (!string.IsNullOrEmpty(f.Advice)) sb.AppendLine("   👉 " + f.Advice);
                    foreach (var line in f.Sample.Take(12)) sb.AppendLine("      " + line);
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Analyse automatique d'un logcat : plantages d'applications (Java, ANR, natifs), système et matériel
    /// (mémoire, stockage, batterie, chauffe, redémarrages), réseau et Wi-Fi (déconnexions, authentification,
    /// DHCP, DNS, serveurs injoignables, certificats) et lecteur code-barres (toutes marques : DataWedge,
    /// Honeywell, Datalogic, Newland, Urovo…). Fonction pure, sans interface : testable isolément.
    /// </summary>
    public static class LogAnalyzer
    {
        public static string CatCrash => Loc.T("Plantages d'applications");
        public static string CatSystem => Loc.T("Système et matériel");
        public static string CatNetwork => Loc.T("Réseau et Wi-Fi");
        public static string CatScanner => Loc.T("Lecteur code-barres");
        public static string CatOverview => Loc.T("Vue d'ensemble");

        private const RegexOptions O = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        private const RegexOptions OI = O | RegexOptions.IgnoreCase;

        private static readonly Regex ProcessLine = new Regex(@"^Process:\s*(\S+?),\s*PID", O);
        private static readonly Regex ExceptionLine = new Regex(@"^([A-Za-z_$][\w$]*(?:\.[\w$]+)*(?:Exception|Error|Throwable)[\w$]*)(?::\s*(.*))?$", O);
        private static readonly Regex CausedBy = new Regex(@"^Caused by:\s*([\w$.]+)(?::\s*(.*))?$", O);
        private static readonly Regex AnrIn = new Regex(@"^ANR in (\S+)", O);
        private static readonly Regex AnrReason = new Regex(@"^Reason:\s*(.*)$", O);
        private static readonly Regex NativeHeader = new Regex(@"pid: \d+, tid: \d+, name: .*?>>> (\S+) <<<", O);
        private static readonly Regex NativeSignal = new Regex(@"signal (\d+) \((\w+)\)", O);
        private static readonly Regex LibcFatal = new Regex(@"Fatal signal \d+ \((\w+)\).*?pid \d+ \(([^)]+)\)", O);
        private static readonly Regex ServiceRestart = new Regex(@"Scheduling restart of crashed service (\S+?)/", O);
        private static readonly Regex LmkKill = new Regex(@"Kill(?:ing)? '([^']+)'", O);
        private static readonly Regex Healthd = new Regex(@"battery l=(\d+) v=(\d+) t=(-?[\d.]+) h=(\d+)", O);
        private static readonly Regex Reboot = new Regex(@"Rebooting, reason:\s*(.*)$|Shutting down|reboot reason", OI);
        private static readonly Regex Thermal = new Regex(@"throttl|thermal (?:status|mitigation|shutdown|warning)|Thermal status|overheat", OI);
        private static readonly Regex Storage = new Regex(@"No space left on device|ENOSPC|storage (?:is )?(?:low|full)|Low storage|low on (?:internal )?storage", OI);
        private static readonly Regex WifiDisc = new Regex(@"CTRL-EVENT-DISCONNECTED bssid=(\S+) reason=(\d+)(\s+locally_generated=1)?", O);
        private static readonly Regex WifiConn = new Regex(@"CTRL-EVENT-CONNECTED - Connection to (\S+) completed", O);
        private static readonly Regex WifiAuth = new Regex(@"CTRL-EVENT-SSID-TEMP-DISABLED.*?reason=(\S+)|CTRL-EVENT-AUTH-REJECT|CTRL-EVENT-ASSOC-REJECT|4-Way Handshake failed|WRONG_KEY|CTRL-EVENT-EAP-FAILURE", O);
        private static readonly Regex Dhcp = new Regex(@"PROVISIONING_FAIL|onProvisioningFailure|DHCP (?:timeout|failed|failure)|dhcp.*?(?:timed? ?out|failed)|No DHCP", OI);
        private static readonly Regex Dns = new Regex(@"Unable to resolve host|UnknownHostException", O);
        private static readonly Regex DnsHost1 = new Regex(@"Unable to resolve host\s+""?([^"":\s]+)", O);
        private static readonly Regex DnsHost2 = new Regex(@"UnknownHostException:\s*""?([^"":\s]+)", O);
        private static readonly Regex Connect = new Regex(@"failed to connect to|ConnectException|SocketTimeoutException|ECONNREFUSED|ETIMEDOUT|EHOSTUNREACH|ENETUNREACH", O);
        private static readonly Regex ConnectHost = new Regex(@"failed to connect to (\S+?)(?:/[\w.:]+)?(?: \(port (\d+)\))?(?:\s|$)", O);
        private static readonly Regex Ssl = new Regex(@"SSLHandshakeException|CertPathValidatorException|Trust anchor for certification path not found|CERTIFICATE_VERIFY_FAILED", O);
        private static readonly Regex ScannerTag = new Regex(@"datawedge|dwapi|scanapi|emdk|barcode|scanner|scanservice|scanmanager|aidc|honeywell|datalogic|newland|^nls|urovo|imager|symbol\.scan|zebra\.scan", OI);
        private static readonly Regex ScannerMsg = new Regex(@"datawedge|barcode|scan engine|\bscanner\b", OI);
        // « Scanner » Wi-Fi, Bluetooth, média… : rien à voir avec le lecteur code-barres.
        private static readonly Regex NotBarcode = new Regex(@"wifi|wlan|bluetooth|\bble|media|location|gnss|gps|nfc|bt_|network|cell", OI);
        // Journaux du système qui citent le lecteur sans qu'il soit en cause (suivi des processus, installation…).
        private static readonly Regex SystemTag = new Regex(@"^(?:ProcessStats|ActivityManager|ActivityTaskManager|PackageManager|PackageInstaller|ApkAssets|AppOps|BroadcastQueue|JobScheduler.*|WindowManager|InputMethodManagerService|Zygote|SELinux|audit|ndroid\..*|.*\.settings|ContentProvider.*|ActivityThread|ApplicationPackageManager)$", OI);
        private static readonly Regex ErrorWords = new Regex(@"error|fail|exception|not found|invalid|unable|timeout|timed out|denied|refused|cannot|can't", OI);
        private static readonly Regex Digits = new Regex(@"0x[0-9a-fA-F]+|\d+", O);

        public static LogAnalysis Analyze(IList<LogEntry> entries, string source = "")
        {
            var a = new LogAnalysis { Source = source ?? "" };
            if (entries == null || entries.Count == 0) return a;
            a.LinesAnalyzed = entries.Count;
            a.FirstTime = entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Time))?.Time ?? "";
            a.LastTime = entries.LastOrDefault(e => !string.IsNullOrEmpty(e.Time))?.Time ?? "";

            var map = new Dictionary<string, LogFinding>();
            var lmkVictims = new Dictionary<string, int>();
            var wifiBssids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int wifiConnections = 0, wifiDisconnections = 0;
            LogEntry firstConn = null; string firstConnTime = "", lastConnTime = "";
            int battMin = 101; double battMaxTemp = double.MinValue; string battBadHealth = null; LogEntry battEntry = null;
            var errorTags = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e == null) continue;
                string msg = e.Message ?? "", tag = e.Tag ?? "", lvl = e.Level ?? "";
                bool isError = lvl == "E" || lvl == "F";
                bool isWarnOrError = isError || lvl == "W";
                if (isError && tag.Length > 0) { errorTags.TryGetValue(tag, out int n); errorTags[tag] = n + 1; }

                // ---------------- Plantages d'applications
                if (tag == "AndroidRuntime" && msg.StartsWith("FATAL EXCEPTION", StringComparison.Ordinal))
                {
                    bool systemProcess = msg.Contains("SYSTEM PROCESS");
                    string pkg = "", exType = "", exMsg = "", cause = "";
                    var sample = new List<string> { e.RawLine ?? msg };
                    for (int j = i + 1; j < entries.Count && j < i + 120; j++)
                    {
                        var s = entries[j];
                        if (s == null || s.Tag != "AndroidRuntime" || (!string.IsNullOrEmpty(e.PID) && s.PID != e.PID)) continue; // lignes d'autres processus intercalées
                        if ((s.Message ?? "").StartsWith("FATAL EXCEPTION", StringComparison.Ordinal)) break;
                        string m = (s.Message ?? "").Trim();
                        if (sample.Count < 30) sample.Add(s.RawLine ?? m);
                        Match mm;
                        if (pkg.Length == 0 && (mm = ProcessLine.Match(m)).Success) pkg = mm.Groups[1].Value;
                        else if (exType.Length == 0 && (mm = ExceptionLine.Match(m)).Success) { exType = mm.Groups[1].Value; exMsg = mm.Groups[2].Value; }
                        else if ((mm = CausedBy.Match(m)).Success) cause = mm.Groups[1].Value + (mm.Groups[2].Success && mm.Groups[2].Value.Length > 0 ? ": " + mm.Groups[2].Value : "");
                    }
                    if (systemProcess)
                    {
                        var f = Add(map, "sysserver", i, e, CatSystem, 0, FindingSeverity.Bad, () => new LogFinding
                        {
                            Title = Loc.T("Plantage du système Android (system_server)"),
                            Detail = Shorten(exType + (exMsg.Length > 0 ? ": " + exMsg : "")),
                            Advice = Loc.T("L'interface du terminal a redémarré. Si cela se répète : mise à jour du système ou retour constructeur."),
                        });
                        if (f.Sample.Count == 0) f.Sample.AddRange(sample);
                    }
                    else
                    {
                        string shortEx = ShortName(exType);
                        var f = Add(map, "crash|" + pkg + "|" + exType, i, e, CatCrash, 0, FindingSeverity.Bad, () => new LogFinding
                        {
                            Title = Loc.T("Plantage de l'application"),
                            App = pkg,
                            Detail = Shorten((shortEx.Length > 0 ? shortEx : Loc.T("Exception")) + (exMsg.Length > 0 ? " : " + exMsg : "")
                                     + (cause.Length > 0 ? "\n" + Loc.F("Cause : {0}", cause) : "")),
                            Advice = Loc.T("Mettre à jour ou réinstaller l'application (vider ses données si besoin). Si le plantage se répète, transmettre ces lignes à l'éditeur."),
                        });
                        if (f.Sample.Count == 0) f.Sample.AddRange(sample);
                    }
                    continue;
                }

                Match m1;
                if ((m1 = AnrIn.Match(msg)).Success && (tag == "ActivityManager" || tag.StartsWith("ActivityManager", StringComparison.Ordinal) || tag == "AnrManager"))
                {
                    string pkg = m1.Groups[1].Value, reason = "";
                    var sample = new List<string> { e.RawLine ?? msg };
                    for (int j = i + 1; j < entries.Count && j < i + 25; j++)
                    {
                        if (entries[j] == null || entries[j].Tag != tag) continue;
                        if (sample.Count < 12) sample.Add(entries[j].RawLine ?? entries[j].Message);
                        var rm = AnrReason.Match((entries[j].Message ?? "").Trim());
                        if (rm.Success) { reason = rm.Groups[1].Value; break; }
                    }
                    var f = Add(map, "anr|" + pkg, i, e, CatCrash, 0, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Application figée (ANR)"),
                        App = pkg,
                        Detail = reason.Length > 0 ? Shorten(Loc.F("Raison : {0}", reason)) : "",
                        Advice = Loc.T("L'application n'a pas répondu pendant plus de 5 secondes. Vérifier la charge du terminal, la mémoire libre, le réseau, puis mettre à jour l'application."),
                    });
                    if (f.Sample.Count == 0) f.Sample.AddRange(sample);
                    continue;
                }

                if ((m1 = LibcFatal.Match(msg)).Success || ((tag == "DEBUG" || tag.StartsWith("crash_dump", StringComparison.Ordinal)) && (m1 = NativeHeader.Match(msg)).Success))
                {
                    string proc, sig = "";
                    if (m1.Groups.Count >= 3 && m1.Groups[2].Success && msg.Contains("Fatal signal")) { sig = m1.Groups[1].Value; proc = m1.Groups[2].Value; }
                    else
                    {
                        proc = m1.Groups[1].Value;
                        for (int j = i + 1; j < entries.Count && j < i + 10; j++)
                        {
                            var sm = NativeSignal.Match(entries[j]?.Message ?? "");
                            if (sm.Success) { sig = sm.Groups[2].Value; break; }
                        }
                    }
                    // Un même plantage natif est écrit par libc puis par DEBUG : compté une seule fois.
                    Add(map, "native|" + proc, i, e, CatCrash, 0, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Plantage natif (bibliothèque système ou pilote)"),
                        App = proc,
                        Detail = sig.Length > 0 ? Loc.F("Signal {0} ({1})", sig, SignalLabel(sig)) : "",
                        Advice = Loc.T("Plantage dans du code natif (pilote, bibliothèque). Mettre à jour l'application et le système ; si cela persiste, bugreport à transmettre au constructeur."),
                    }, mergeWindow: 150);
                    continue;
                }

                if ((m1 = ServiceRestart.Match(msg)).Success)
                {
                    Add(map, "svc|" + m1.Groups[1].Value, i, e, CatCrash, 0, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Service redémarré après un plantage"),
                        App = m1.Groups[1].Value,
                        Advice = Loc.T("Un service en arrière-plan a planté puis a été relancé par Android. À surveiller s'il revient souvent."),
                    });
                    continue;
                }

                // ---------------- Système et matériel
                if (tag == "Watchdog" && msg.Contains("WATCHDOG KILLING SYSTEM PROCESS"))
                {
                    Add(map, "watchdog", i, e, CatSystem, 1, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Blocage du système (watchdog)"),
                        Detail = Shorten(msg),
                        Advice = Loc.T("Android était bloqué et a redémarré son interface. Si cela se répète : mise à jour du système ou retour constructeur."),
                    });
                    continue;
                }

                if ((tag.IndexOf("lowmemorykiller", StringComparison.OrdinalIgnoreCase) >= 0 || tag == "lmkd") && (m1 = LmkKill.Match(msg)).Success)
                {
                    string victim = m1.Groups[1].Value;
                    lmkVictims.TryGetValue(victim, out int n); lmkVictims[victim] = n + 1;
                    Add(map, "lmk", i, e, CatSystem, 1, FindingSeverity.Info, () => new LogFinding
                    {
                        Title = Loc.T("Mémoire saturée : applications fermées par Android"),
                        Advice = Loc.T("La mémoire vive manque. Fermer les applications inutiles, désactiver celles qui tournent en arrière-plan, ou redémarrer le terminal."),
                    });
                    continue;
                }

                if (Storage.IsMatch(msg))
                {
                    Add(map, "storage", i, e, CatSystem, 1, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Stockage plein ou presque plein"),
                        Detail = Shorten(msg),
                        Advice = Loc.T("Libérer de la place : supprimer les captures et fichiers inutiles (Explorer), vider le cache des applications (Package Manager)."),
                    }, mergeWindow: 20);
                    continue;
                }

                if (tag == "healthd" && (m1 = Healthd.Match(msg)).Success)
                {
                    int level = int.Parse(m1.Groups[1].Value, CultureInfo.InvariantCulture);
                    double temp = double.Parse(m1.Groups[3].Value, CultureInfo.InvariantCulture);
                    if (temp > 100) temp /= 10.0; // certains terminaux écrivent des dixièmes de degré
                    int health = int.Parse(m1.Groups[4].Value, CultureInfo.InvariantCulture);
                    if (battEntry == null) battEntry = e;
                    battMin = Math.Min(battMin, level);
                    battMaxTemp = Math.Max(battMaxTemp, temp);
                    if (health != 2 && health != 1 && battBadHealth == null) battBadHealth = HealthLabel(health);
                    continue;
                }

                if (tag.IndexOf("ShutdownThread", StringComparison.OrdinalIgnoreCase) >= 0 && (m1 = Reboot.Match(msg)).Success)
                {
                    string reason = m1.Groups[1].Success ? m1.Groups[1].Value.Trim() : "";
                    Add(map, "reboot", i, e, CatSystem, 1, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Redémarrage ou arrêt du terminal"),
                        Detail = reason.Length > 0 ? Loc.F("Raison : {0}", reason) : "",
                        Advice = Loc.T("Vérifier s'il a été demandé par l'utilisateur ; sinon, voir les plantages et la batterie juste avant."),
                    }, mergeWindow: 50);
                    continue;
                }

                if (isWarnOrError && Thermal.IsMatch(msg + " " + tag) && (tag.IndexOf("thermal", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("thermal", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("throttl", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    Add(map, "thermal", i, e, CatSystem, 1, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Échauffement : performances réduites"),
                        Detail = Shorten(msg),
                        Advice = Loc.T("Le terminal chauffe : éviter la charge en utilisation intensive, vérifier la batterie et l'environnement (soleil, véhicule)."),
                    }, mergeWindow: 20);
                    continue;
                }

                // ---------------- Réseau et Wi-Fi
                if ((m1 = WifiDisc.Match(msg)).Success)
                {
                    wifiDisconnections++;
                    string code = m1.Groups[2].Value;
                    bool local = m1.Groups[3].Success && m1.Groups[3].Value.Length > 0;
                    Add(map, "wifidisc|" + code + "|" + local, i, e, CatNetwork, 2, FindingSeverity.Info, () => new LogFinding
                    {
                        Title = Loc.T("Déconnexion Wi-Fi"),
                        Detail = Loc.F("Code {0} : {1}", code, WifiReasonLabel(code)) + (local ? " — " + Loc.T("à l'initiative du terminal") : " — " + Loc.T("à l'initiative de la borne")),
                        Advice = WifiReasonAdvice(code),
                    });
                    continue;
                }
                if ((m1 = WifiConn.Match(msg)).Success)
                {
                    wifiConnections++;
                    wifiBssids.Add(m1.Groups[1].Value);
                    if (firstConn == null) { firstConn = e; firstConnTime = e.Time ?? ""; }
                    lastConnTime = e.Time ?? "";
                    continue;
                }
                if ((m1 = WifiAuth.Match(msg)).Success)
                {
                    string reason = m1.Groups[1].Success ? m1.Groups[1].Value : "";
                    if (reason.Length == 0)
                    {
                        if (msg.Contains("WRONG_KEY") || msg.Contains("4-Way")) reason = "WRONG_KEY";
                        else if (msg.Contains("EAP-FAILURE")) reason = "EAP";
                        else if (msg.Contains("ASSOC-REJECT")) reason = "ASSOC_REJECT";
                        else reason = "AUTH_REJECT";
                    }
                    Add(map, "wifiauth|" + reason, i, e, CatNetwork, 2, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Échec de connexion au réseau Wi-Fi"),
                        Detail = WifiAuthLabel(reason),
                        Advice = Loc.T("Vérifier le mot de passe ou le certificat du réseau, la date et l'heure du terminal, et la configuration de la borne (sécurité, filtrage MAC)."),
                    });
                    continue;
                }
                if (Dhcp.IsMatch(msg) && (isWarnOrError || tag.IndexOf("dhcp", StringComparison.OrdinalIgnoreCase) >= 0 || tag.StartsWith("IpClient", StringComparison.Ordinal)))
                {
                    Add(map, "dhcp", i, e, CatNetwork, 2, FindingSeverity.Bad, () => new LogFinding
                    {
                        Title = Loc.T("Pas d'adresse IP obtenue (DHCP)"),
                        Detail = Shorten(msg),
                        Advice = Loc.T("Le terminal est associé à la borne mais n'obtient pas d'adresse : vérifier le serveur DHCP (plage pleine ?) et le VLAN du réseau."),
                    }, mergeWindow: 5);
                    continue;
                }
                if (isWarnOrError && (m1 = Dns.Match(msg)).Success)
                {
                    var hm = DnsHost1.Match(msg);
                    if (!hm.Success) hm = DnsHost2.Match(msg);
                    string host = hm.Success && !hm.Groups[1].Value.StartsWith("Unable", StringComparison.Ordinal) ? hm.Groups[1].Value : "";
                    Add(map, "dns|" + host, i, e, CatNetwork, 2, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Nom de serveur introuvable (DNS)"),
                        App = host,
                        Advice = Loc.T("Le terminal n'arrive pas à trouver ce serveur : vérifier la connexion (Wi-Fi ou données), les serveurs DNS et l'adresse configurée dans l'application."),
                    }, mergeWindow: 30);
                    continue;
                }
                if (isWarnOrError && (m1 = Ssl.Match(msg)).Success)
                {
                    Add(map, "ssl", i, e, CatNetwork, 2, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Erreur de certificat (connexion sécurisée)"),
                        Detail = Shorten(msg),
                        Advice = Loc.T("Vérifier la date et l'heure du terminal, puis le certificat du serveur ou du proxy (certificat d'entreprise à installer ?)."),
                    }, mergeWindow: 30);
                    continue;
                }
                if (isWarnOrError && (m1 = Connect.Match(msg)).Success)
                {
                    var hm = ConnectHost.Match(msg);
                    string host = hm.Success ? hm.Groups[1].Value : "";
                    if (host.Length > 0 && hm.Groups[2].Success && hm.Groups[2].Value.Length > 0) host += ":" + hm.Groups[2].Value;
                    string type = host.Length > 0 ? "" : m1.Value;
                    Add(map, "connect|" + host + "|" + type, i, e, CatNetwork, 2, FindingSeverity.Warn, () => new LogFinding
                    {
                        Title = Loc.T("Serveur injoignable ou trop lent"),
                        App = host,
                        Detail = host.Length > 0 ? "" : ConnectLabel(type),
                        Advice = Loc.T("Vérifier que le serveur est accessible depuis le réseau du terminal (pare-feu, proxy, VPN) et que le signal Wi-Fi est suffisant."),
                    }, mergeWindow: 30);
                    continue;
                }

                // ---------------- Lecteur code-barres
                // Journal du lecteur lui-même (tag), ou erreur d'une application qui cite le lecteur ; pas les
                // journaux du système qui le mentionnent seulement (suivi des processus, installation…).
                bool scannerTag = ScannerTag.IsMatch(tag) && !NotBarcode.IsMatch(tag);
                bool scannerMsg = !scannerTag && isError && ScannerMsg.IsMatch(msg) && ErrorWords.IsMatch(msg) && !NotBarcode.IsMatch(tag);
                if (isWarnOrError && (scannerTag || scannerMsg) && !SystemTag.IsMatch(tag))
                {
                    string norm = Digits.Replace(msg.Trim(), "#");
                    if (norm.Length > 120) norm = norm.Substring(0, 120);
                    // Simple avertissement sans mot d'erreur (changement d'état…) : information, pas une alerte.
                    var sev = isError ? FindingSeverity.Bad : ErrorWords.IsMatch(msg) ? FindingSeverity.Warn : FindingSeverity.Info;
                    Add(map, "scan|" + lvl + "|" + tag + "|" + norm, i, e, CatScanner, 3, sev, () => new LogFinding
                    {
                        Title = sev == FindingSeverity.Bad ? Loc.T("Erreur du lecteur code-barres")
                              : sev == FindingSeverity.Warn ? Loc.T("Avertissement du lecteur code-barres") : Loc.T("Message du lecteur code-barres"),
                        App = tag,
                        Detail = Shorten(msg),
                        Advice = Loc.T("Vérifier le profil du lecteur associé à l'application (DataWedge, Scanner Settings…), puis redémarrer le service du lecteur ou le terminal."),
                    });
                    continue;
                }
            }

            // ---------------- Synthèses
            if (map.TryGetValue("lmk", out var lmk))
            {
                lmk.Severity = lmk.Count >= 5 ? FindingSeverity.Warn : FindingSeverity.Info;
                lmk.Detail = Loc.F("Les plus touchées : {0}", string.Join(", ", lmkVictims.OrderByDescending(p => p.Value).Take(4).Select(p => p.Key + " (" + p.Value + ")")));
            }

            if (wifiDisconnections >= 3)
                foreach (var w in map.Values.Where(x => x.Key.StartsWith("wifidisc|", StringComparison.Ordinal)))
                    w.Severity = FindingSeverity.Warn;

            if (wifiConnections > 0)
            {
                var f = new LogFinding
                {
                    Key = "wificonn", Category = CatNetwork, CategoryOrder = 2,
                    Severity = wifiConnections >= 10 && wifiBssids.Count > 1 ? FindingSeverity.Warn : FindingSeverity.Info,
                    Title = wifiBssids.Count > 1 ? Loc.T("Itinérance Wi-Fi (changements de borne)") : Loc.T("Connexions Wi-Fi"),
                    Detail = Loc.F("{0} connexion(s) vers {1} borne(s) différente(s)", wifiConnections, wifiBssids.Count),
                    Advice = wifiConnections >= 10 && wifiBssids.Count > 1 ? Loc.T("Nombreux changements de borne : vérifier la couverture et les réglages d'itinérance (l'Analyse Wi-Fi d'ADB Tools aide à le mesurer).") : "",
                    Count = wifiConnections, FirstEntry = firstConn, FirstTime = firstConnTime, LastTime = lastConnTime,
                };
                map[f.Key] = f;
            }

            if (battEntry != null)
            {
                FindingSeverity sev = FindingSeverity.Info;
                string title = Loc.T("Batterie");
                string advice = "";
                if (battBadHealth != null) { sev = FindingSeverity.Bad; title = Loc.T("Batterie signalée en mauvais état"); advice = Loc.T("Contrôler la batterie (Diagnostics SAV) et la remplacer si besoin."); }
                else if (battMaxTemp >= 50) { sev = FindingSeverity.Bad; title = Loc.T("Batterie très chaude"); advice = Loc.T("Température excessive : retirer le terminal de la charge, le laisser refroidir et contrôler la batterie."); }
                else if (battMaxTemp >= 45) { sev = FindingSeverity.Warn; title = Loc.T("Batterie chaude"); advice = Loc.T("Température élevée : surveiller la charge et l'environnement du terminal."); }
                else if (battMin <= 5) { sev = FindingSeverity.Warn; title = Loc.T("Batterie presque vide"); advice = Loc.T("Le terminal a fonctionné avec une batterie presque vide : les arrêts ou ralentissements peuvent venir de là."); }
                string temp = battMaxTemp > double.MinValue ? battMaxTemp.ToString("0.#", CultureInfo.CurrentCulture) : "?";
                var f = new LogFinding
                {
                    Key = "battery", Category = CatSystem, CategoryOrder = 1, Severity = sev, Title = title,
                    Detail = Loc.F("Niveau le plus bas : {0} % · température maximale : {1} °C", battMin, temp) + (battBadHealth != null ? "\n" + Loc.F("État : {0}", battBadHealth) : ""),
                    Advice = advice, Count = 1, FirstEntry = battEntry, FirstTime = battEntry.Time ?? "", LastTime = battEntry.Time ?? "",
                };
                map[f.Key] = f;
            }

            var top = errorTags.OrderByDescending(p => p.Value).Take(5).ToList();
            if (top.Count > 0)
            {
                map["overview"] = new LogFinding
                {
                    Key = "overview", Category = CatOverview, CategoryOrder = 4, Severity = FindingSeverity.Info,
                    Title = Loc.T("Sources d'erreurs les plus fréquentes"),
                    Detail = string.Join("\n", top.Select(p => Loc.F("{0} : {1} ligne(s) en erreur", p.Key, p.Value))),
                    Count = 1,
                };
            }

            a.Findings.AddRange(map.Values
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.CategoryOrder)
                .ThenByDescending(f => f.Count));
            return a;
        }

        // ------------------------------------------------------------------ Outils

        private static LogFinding Add(Dictionary<string, LogFinding> map, string key, int index, LogEntry e, string category, int order,
                                      FindingSeverity severity, Func<LogFinding> create, int mergeWindow = 0)
        {
            if (!map.TryGetValue(key, out var f))
            {
                f = create();
                f.Key = key; f.Category = category; f.CategoryOrder = order; f.Severity = severity;
                f.FirstEntry = e; f.FirstTime = e.Time ?? "";
                map[key] = f;
            }
            // Même événement écrit sur plusieurs lignes proches (pile d'appels, libc + DEBUG…) : compté une fois.
            if (f.Count == 0 || mergeWindow <= 0 || index - f.LastIndex > mergeWindow) f.Count++;
            f.LastIndex = index;
            if (!string.IsNullOrEmpty(e.Time)) f.LastTime = e.Time;
            return f;
        }

        private static string Shorten(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 240 ? s.Substring(0, 237) + "…" : s;
        }

        private static string ShortName(string type)
        {
            if (string.IsNullOrEmpty(type)) return "";
            int dot = type.LastIndexOf('.');
            return dot >= 0 ? type.Substring(dot + 1) : type;
        }

        internal static string SignalLabel(string sig)
        {
            switch (sig)
            {
                case "SIGSEGV": return Loc.T("accès mémoire invalide");
                case "SIGABRT": return Loc.T("arrêt volontaire après une erreur");
                case "SIGBUS": return Loc.T("erreur d'accès mémoire");
                case "SIGILL": return Loc.T("instruction invalide");
                case "SIGFPE": return Loc.T("erreur de calcul");
                default: return Loc.T("signal système");
            }
        }

        private static string HealthLabel(int h)
        {
            switch (h)
            {
                case 3: return Loc.T("surchauffe");
                case 4: return Loc.T("batterie morte");
                case 5: return Loc.T("surtension");
                case 6: return Loc.T("défaillance");
                case 7: return Loc.T("trop froide");
                default: return Loc.T("inconnu");
            }
        }

        internal static string WifiReasonLabel(string code)
        {
            switch (code)
            {
                case "1": return Loc.T("raison non précisée");
                case "2": return Loc.T("authentification expirée");
                case "3": return Loc.T("déconnexion volontaire (sortie du réseau)");
                case "4": return Loc.T("inactivité");
                case "5": return Loc.T("borne surchargée");
                case "6":
                case "7": return Loc.T("trame refusée par la borne");
                case "8": return Loc.T("départ vers une autre borne ou arrêt du Wi-Fi");
                case "9": return Loc.T("association non autorisée");
                case "14": return Loc.T("erreur d'intégrité (clé)");
                case "15": return Loc.T("délai dépassé à l'échange de clés (mauvais mot de passe ?)");
                case "16": return Loc.T("délai dépassé à la mise à jour des clés");
                case "23": return Loc.T("échec de l'authentification 802.1X");
                case "34": return Loc.T("trop de trames perdues (signal faible)");
                default: return Loc.T("voir la norme 802.11");
            }
        }

        private static string WifiReasonAdvice(string code)
        {
            switch (code)
            {
                case "15":
                case "16":
                case "23":
                case "2":
                case "14": return Loc.T("Problème d'authentification : vérifier le mot de passe ou le certificat, et la date du terminal.");
                case "4":
                case "34": return Loc.T("Signal faible ou zone mal couverte : vérifier la couverture Wi-Fi (Analyse Wi-Fi).");
                case "5": return Loc.T("La borne refuse des clients : trop de terminaux connectés.");
                case "3":
                case "8": return Loc.T("Généralement normal (itinérance, mise en veille, Wi-Fi coupé) ; à surveiller si très fréquent.");
                default: return Loc.T("À surveiller si les déconnexions sont fréquentes : couverture, réglages de la borne.");
            }
        }

        private static string WifiAuthLabel(string reason)
        {
            switch (reason)
            {
                case "WRONG_KEY": return Loc.T("Mot de passe Wi-Fi refusé");
                case "AUTH_FAILED": return Loc.T("Authentification refusée");
                case "EAP":
                case "EAP_FAILURE": return Loc.T("Échec de l'authentification entreprise (802.1X / certificat)");
                case "ASSOC_REJECT": return Loc.T("Association refusée par la borne");
                case "CONN_FAILED": return Loc.T("Connexion échouée");
                default: return Loc.F("Refus de la borne ({0})", reason);
            }
        }

        private static string ConnectLabel(string type)
        {
            if (type.Contains("Timeout") || type == "ETIMEDOUT") return Loc.T("Délai de connexion dépassé");
            if (type.Contains("REFUSED")) return Loc.T("Connexion refusée par le serveur");
            if (type.Contains("UNREACH")) return Loc.T("Réseau ou serveur inaccessible");
            return Loc.T("Connexion impossible");
        }
    }
}
