using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ADBToolsNew.Core
{
    /// <summary>Une lecture reçue de l'appli de test du terminal (ADBToolsScan).</summary>
    public sealed class ScanRead
    {
        public int Index { get; set; }
        /// <summary>Heure de réception sur le PC (les horloges du PC et du terminal peuvent différer).</summary>
        public DateTime Time { get; set; }
        public string Data { get; set; } = "";
        /// <summary>keyboard, datawedge ou honeywell.</summary>
        public string Source { get; set; } = "";
        /// <summary>Symbologie brute donnée par le lecteur (vide en mode clavier).</summary>
        public string RawSymbology { get; set; } = "";
        public double? TriggerDelayMs { get; set; }
        /// <summary>null : pas de contrôle ; vrai/faux : conforme ou non au code attendu.</summary>
        public bool? Expected { get; set; }

        public string TimeText => Time.ToString("HH:mm:ss");
        public string DataDisplay => ScanText.Visible(Data);
        public int Length => Data.Length;
        public string Symbology => string.IsNullOrEmpty(RawSymbology) ? ScanText.GuessSymbology(Data) : ScanText.FriendlySymbology(RawSymbology);
        public string SourceText => Source == "datawedge" ? "DataWedge" : Source == "honeywell" ? "Honeywell" : Loc.T("Clavier");
        public string DelayText => TriggerDelayMs.HasValue ? Loc.F("{0:0} ms", TriggerDelayMs.Value) : "";
        public string CheckIcon => Expected == true ? "✅" : Expected == false ? "❌" : "";
    }

    /// <summary>Lecture des lignes du journal et mise en forme des codes lus. Fonctions pures, testables.</summary>
    public static class ScanText
    {
        public const string LogTag = "ADBToolsScan";

        /// <summary>
        /// Ligne du journal « … ADBToolsScan: SCAN \t ms \t source \t symbologie \t base64 » → lecture.
        /// Renvoie null pour toute autre ligne.
        /// </summary>
        public static ScanRead ParseLogLine(string line, DateTime receivedAt)
        {
            if (string.IsNullOrEmpty(line)) return null;
            int i = line.IndexOf("SCAN\t", StringComparison.Ordinal);
            if (i < 0 || line.IndexOf(LogTag, StringComparison.Ordinal) < 0) return null;
            var parts = line.Substring(i).TrimEnd('\r', '\n').Split('\t');
            if (parts.Length < 5) return null;
            string data;
            try { data = Encoding.UTF8.GetString(Convert.FromBase64String(parts[4].Trim())); }
            catch (FormatException) { return null; }
            return new ScanRead { Time = receivedAt, Source = parts[2].Trim(), RawSymbology = parts[3].Trim(), Data = data };
        }

        /// <summary>Caractères invisibles rendus visibles : ⟨TAB⟩, ⟨CR⟩, ⟨GS⟩ (séparateur GS1)…</summary>
        public static string Visible(string data)
        {
            if (string.IsNullOrEmpty(data)) return "";
            var sb = new StringBuilder();
            foreach (char c in data)
            {
                switch (c)
                {
                    case '\t': sb.Append("⟨TAB⟩"); break;
                    case '\r': sb.Append("⟨CR⟩"); break;
                    case '\n': sb.Append("⟨LF⟩"); break;
                    case (char)29: sb.Append("⟨GS⟩"); break;
                    case (char)30: sb.Append("⟨RS⟩"); break;
                    case (char)4: sb.Append("⟨EOT⟩"); break;
                    case (char)27: sb.Append("⟨ESC⟩"); break;
                    default:
                        if (c < 32 || c == 127) sb.Append("⟨").Append(((int)c).ToString("X2")).Append("⟩");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Nom lisible d'une symbologie DataWedge (LABEL-TYPE-EAN13) ou d'un identifiant Honeywell (codeId).</summary>
        public static string FriendlySymbology(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string r = raw.Trim();
            if (r.StartsWith("LABEL-TYPE-", StringComparison.OrdinalIgnoreCase))
            {
                string t = r.Substring(11).ToUpperInvariant();
                switch (t)
                {
                    case "EAN13": return "EAN-13";
                    case "EAN8": return "EAN-8";
                    case "UPCA": return "UPC-A";
                    case "UPCE0": case "UPCE1": return "UPC-E";
                    case "CODE128": return "Code 128";
                    case "GS1-128": case "EAN128": return "GS1-128";
                    case "CODE39": return "Code 39";
                    case "CODE93": return "Code 93";
                    case "I2OF5": return "Interleaved 2 of 5";
                    case "D2OF5": return "Discrete 2 of 5";
                    case "CODABAR": return "Codabar";
                    case "QRCODE": return "QR Code";
                    case "DATAMATRIX": return "Data Matrix";
                    case "GS1-DATAMATRIX": return "GS1 Data Matrix";
                    case "GS1-QRCODE": return "GS1 QR Code";
                    case "PDF417": return "PDF417";
                    case "MICROPDF": return "MicroPDF417";
                    case "AZTEC": return "Aztec";
                    case "MAXICODE": return "MaxiCode";
                    case "GS1-DATABAR": case "RSS14": return "GS1 DataBar";
                    case "GS1-DATABAR-EXP": case "RSSEXP": return "GS1 DataBar Expanded";
                    case "DOTCODE": return "DotCode";
                    default: return t;
                }
            }
            // Honeywell : identifiant d'une lettre, éventuellement suivi de « / AIM ».
            string id = r.Split('/')[0].Trim();
            switch (id)
            {
                case "d": return "EAN-13";
                case "D": return "EAN-8";
                case "c": return "UPC-A";
                case "E": return "UPC-E";
                case "j": return "Code 128";
                case "I": return "GS1-128";
                case "b": return "Code 39";
                case "i": return "Code 93";
                case "e": return "Interleaved 2 of 5";
                case "f": return "Straight 2 of 5";
                case "a": return "Codabar";
                case "s": return "QR Code";
                case "w": return "Data Matrix";
                case "r": return "PDF417";
                case "R": return "MicroPDF417";
                case "z": return "Aztec";
                case "x": return "MaxiCode";
                case "y": return "GS1 DataBar";
                case "]": return Loc.T("inconnue");
                default: return r;
            }
        }

        /// <summary>
        /// Symbologie probable en mode clavier (le lecteur ne la transmet pas) : EAN/UPC reconnus grâce à leur clé
        /// de contrôle, séparateur GS1, URL… Toujours affichée comme « probable ».
        /// </summary>
        public static string GuessSymbology(string data)
        {
            string d = (data ?? "").TrimEnd('\r', '\n', '\t');
            if (d.Length == 0) return "";
            string probable = " (" + Loc.T("probable") + ")";
            bool digits = d.All(char.IsDigit);
            if (digits && d.Length == 13 && CheckDigitOk(d)) return "EAN-13" + probable;
            if (digits && d.Length == 8 && CheckDigitOk(d)) return "EAN-8" + probable;
            if (digits && d.Length == 12 && CheckDigitOk(d)) return "UPC-A" + probable;
            if (d.IndexOf((char)29) >= 0 || d.StartsWith("]C1", StringComparison.Ordinal) || d.StartsWith("]d2", StringComparison.Ordinal) || d.StartsWith("]Q3", StringComparison.Ordinal))
                return "GS1" + probable;
            if (d.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || d.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || d.Length > 80)
                return Loc.T("Code 2D (QR, Data Matrix…)") + probable;
            return Loc.T("non transmise (mode clavier)");
        }

        /// <summary>Clé de contrôle EAN / UPC (modulo 10, poids 3 et 1 en partant de la droite).</summary>
        public static bool CheckDigitOk(string digits)
        {
            int sum = 0;
            for (int i = digits.Length - 2, w = 3; i >= 0; i--, w = 4 - w)
                sum += (digits[i] - '0') * w;
            return (10 - sum % 10) % 10 == digits[digits.Length - 1] - '0';
        }

        /// <summary>Codes attendus saisis un par ligne (planche de test) ; lignes vides ignorées.</summary>
        public static List<string> ParseExpected(string text) =>
            (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
    }

    /// <summary>
    /// Session de test du lecteur : lectures, appuis sur la gâchette (getevent), codes attendus, statistiques.
    /// </summary>
    public sealed class ScanTestSession
    {
        /// <summary>Délai maximal entre l'appui sur la gâchette et la lecture pour les rapprocher.</summary>
        public static readonly TimeSpan TriggerWindow = TimeSpan.FromSeconds(4);

        private readonly List<DateTime> _triggers = new List<DateTime>();
        private readonly HashSet<int> _triggersWithRead = new HashSet<int>();

        public DateTime Start { get; } = DateTime.Now;
        public List<ScanRead> Reads { get; } = new List<ScanRead>();
        public List<string> ExpectedCodes { get; private set; } = new List<string>();
        public string Device { get; set; } = "";

        public void SetExpected(IEnumerable<string> codes)
        {
            ExpectedCodes = (codes ?? Enumerable.Empty<string>()).ToList();
            foreach (var r in Reads) r.Expected = Check(r.Data);
        }

        private bool? Check(string data)
        {
            if (ExpectedCodes.Count == 0) return null;
            string d = (data ?? "").TrimEnd('\r', '\n', '\t');
            return ExpectedCodes.Contains(d);
        }

        public void AddTrigger(DateTime at) => _triggers.Add(at);

        public ScanRead AddRead(ScanRead r)
        {
            r.Index = Reads.Count + 1;
            // Gâchette la plus récente, sans lecture, dans la fenêtre précédant la lecture.
            for (int i = _triggers.Count - 1; i >= 0; i--)
            {
                var t = _triggers[i];
                if (t > r.Time) continue;
                if (r.Time - t > TriggerWindow) break;
                if (_triggersWithRead.Contains(i)) break;
                _triggersWithRead.Add(i);
                r.TriggerDelayMs = (r.Time - t).TotalMilliseconds;
                break;
            }
            r.Expected = Check(r.Data);
            Reads.Add(r);
            return r;
        }

        public int TriggerCount => _triggers.Count;

        /// <summary>Appuis sans lecture (une fois la fenêtre écoulée) : code non lu, mal présenté, ou optique en cause.</summary>
        public int MissedTriggers(DateTime now) =>
            _triggers.Select((t, i) => new { t, i }).Count(x => !_triggersWithRead.Contains(x.i) && now - x.t > TriggerWindow);

        public double? SuccessRate(DateTime now)
        {
            int counted = _triggersWithRead.Count + MissedTriggers(now);
            if (counted == 0) return null;
            return 100.0 * _triggersWithRead.Count / counted;
        }

        public double? AverageDelayMs
        {
            get
            {
                var d = Reads.Where(r => r.TriggerDelayMs.HasValue).Select(r => r.TriggerDelayMs.Value).ToList();
                return d.Count == 0 ? (double?)null : d.Average();
            }
        }

        /// <summary>Lectures par minute, entre la première et la dernière lecture (au moins une minute comptée à partir de 2 lectures).</summary>
        public double? ReadsPerMinute
        {
            get
            {
                if (Reads.Count < 2) return null;
                double minutes = (Reads[Reads.Count - 1].Time - Reads[0].Time).TotalMinutes;
                if (minutes <= 0) return null;
                return (Reads.Count - 1) / minutes;
            }
        }

        public int ExpectedFound => ExpectedCodes.Count(c => Reads.Any(r => (r.Data ?? "").TrimEnd('\r', '\n', '\t') == c));
        public int Mismatches => Reads.Count(r => r.Expected == false);

        /// <summary>Verdict pour le rapport SAV : ✅ / ⚠️ / ❌ et texte de détail.</summary>
        public (string Icon, string Details, string Comment) Verdict(DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Loc.T("--- TEST DU LECTEUR (appli de test ADB Tools) ---"));
            sb.AppendLine(Loc.F("• Lectures : {0}", Reads.Count));
            var sym = Reads.GroupBy(r => r.Symbology).Select(g => g.Key + " ×" + g.Count());
            if (Reads.Count > 0) sb.AppendLine(Loc.F("• Symbologies : {0}", string.Join(", ", sym)));
            if (TriggerCount > 0)
            {
                sb.AppendLine(Loc.F("• Appuis sur la gâchette : {0} — sans lecture : {1}", TriggerCount, MissedTriggers(now)));
                var rate = SuccessRate(now);
                if (rate.HasValue) sb.AppendLine(Loc.F("• Taux de réussite : {0:0} %", rate.Value));
            }
            if (AverageDelayMs.HasValue) sb.AppendLine(Loc.F("• Délai moyen gâchette → lecture : {0:0} ms", AverageDelayMs.Value));
            if (ReadsPerMinute.HasValue) sb.AppendLine(Loc.F("• Cadence : {0:0.0} lectures / minute", ReadsPerMinute.Value));
            if (ExpectedCodes.Count > 0)
                sb.AppendLine(Loc.F("• Codes attendus lus : {0} / {1} — lectures non conformes : {2}", ExpectedFound, ExpectedCodes.Count, Mismatches));

            string icon, comment;
            var r2 = SuccessRate(now);
            if (Reads.Count == 0) { icon = "❌"; comment = Loc.T("Aucune lecture pendant le test."); }
            else if (Mismatches > 0 || (ExpectedCodes.Count > 0 && ExpectedFound < ExpectedCodes.Count)) { icon = "⚠️"; comment = Loc.T("Lectures incomplètes ou non conformes aux codes attendus."); }
            else if (r2.HasValue && r2.Value < 80) { icon = "⚠️"; comment = Loc.F("Taux de réussite faible ({0:0} %) : vérifier la fenêtre et l'optique.", r2.Value); }
            else { icon = "✅"; comment = Loc.F("{0} lecture(s) correctes.", Reads.Count); }
            return (icon, sb.ToString(), comment);
        }

        /// <summary>Export tableur (séparateur « ; »).</summary>
        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", "#", Loc.T("Heure"), Loc.T("Données"), Loc.T("Longueur"), Loc.T("Symbologie"), Loc.T("Source"), Loc.T("Délai gâchette (ms)"), Loc.T("Conforme")));
            foreach (var r in Reads)
                sb.AppendLine(string.Join(";", r.Index, r.TimeText, Csv(r.DataDisplay), r.Length, Csv(r.Symbology), r.SourceText,
                    r.TriggerDelayMs.HasValue ? r.TriggerDelayMs.Value.ToString("0", CultureInfo.InvariantCulture) : "",
                    r.Expected == true ? Loc.T("oui") : r.Expected == false ? Loc.T("non") : ""));
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
