using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Rapport d'analyse d'un relevé de logs, en une page HTML autonome (s'ouvre dans le navigateur, s'imprime en PDF) :
    /// terminal, période analysée, filtres, frise, constats avec les lignes qui les entourent, comparaison éventuelle et,
    /// si elles ne sont pas trop nombreuses, les lignes affichées.
    /// </summary>
    public static class LogReport
    {
        public sealed class Options
        {
            public string Title = "";
            public LogArchive Archive;
            public LogAnalysis Analysis;
            public IList<FileLogEntry> View;
            public int From = int.MinValue, To = int.MaxValue;
            public string ScopeText = "";
            public string FiltersText = "";
            public IList<LogCompareRow> Compare;
            public string CompareText = "";
            public int MaxLines = 3000;
            public string AppVersion = "";
            public IList<LogInsights.BeforeRow> Before;
            public string BeforeTitle = "";
            public IList<LogInsights.Session> Sessions;
            public LogInsights.AppLife App;
            public IList<LogInsights.Bookmark> Bookmarks;
        }

        private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        public static string BuildHtml(Options o)
        {
            var a = o.Archive;
            var sb = new StringBuilder();
            string title = Loc.T("Rapport d'analyse de logs");
            sb.Append("<!DOCTYPE html><html lang=\"").Append(Loc.IsEnglish ? "en" : "fr").Append("\"><head><meta charset=\"utf-8\">")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(H(title + " — " + o.Title)).Append("</title><style>")
              .Append(Css).Append("</style></head><body><main>");

            // En-tête
            sb.Append("<header><div class=\"brand\">ADB Tools Pro").Append(o.AppVersion.Length > 0 ? " " + H(o.AppVersion) : "").Append("</div>")
              .Append("<h1>").Append(H(title)).Append("</h1>")
              .Append("<p class=\"sub\">").Append(H(o.Title)).Append(" · ").Append(H(Loc.F("généré le {0:dd/MM/yyyy à HH:mm}", DateTime.Now))).Append("</p></header>");

            // Synthèse
            var findings = o.Analysis?.Findings ?? new List<LogFinding>();
            int bad = findings.Count(f => f.Severity == FindingSeverity.Bad), warn = findings.Count(f => f.Severity == FindingSeverity.Warn);
            int incidents = findings.Count(f => f.Category == LogAnalyzer.CatIncident);
            sb.Append("<section class=\"cards\">");
            Card(sb, Loc.T("Période analysée"), o.ScopeText);
            Card(sb, Loc.T("Lignes analysées"), (o.Analysis?.LinesAnalyzed ?? 0).ToString("N0", CultureInfo.CurrentCulture));
            Card(sb, Loc.T("Problèmes importants"), bad.ToString(CultureInfo.CurrentCulture), bad > 0 ? "bad" : "ok");
            Card(sb, Loc.T("Points à surveiller"), warn.ToString(CultureInfo.CurrentCulture), warn > 0 ? "warn" : "ok");
            if (incidents > 0) Card(sb, Loc.T("Incidents signalés"), incidents.ToString(CultureInfo.CurrentCulture), "warn");
            sb.Append("</section>");

            // Terminal
            if (a != null && a.Device.Count > 0)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Terminal"))).Append("</h2><table class=\"kv\">");
                foreach (var p in a.Device) sb.Append("<tr><th>").Append(H(p.Key)).Append("</th><td>").Append(H(p.Value)).Append("</td></tr>");
                sb.Append("</table></section>");
            }

            // Source et filtres
            sb.Append("<section><h2>").Append(H(Loc.T("Relevé analysé"))).Append("</h2><table class=\"kv\">");
            if (a != null)
            {
                sb.Append("<tr><th>").Append(H(Loc.T("Fichier"))).Append("</th><td>").Append(H(a.Title)).Append("</td></tr>");
                sb.Append("<tr><th>").Append(H(Loc.T("Journaux"))).Append("</th><td>")
                  .Append(H(string.Join(", ", a.LogFiles.Where(f => f.Included).Select(f => f.Name)))).Append("</td></tr>");
                if (a.SpanFrom >= 0)
                    sb.Append("<tr><th>").Append(H(Loc.T("Période du relevé"))).Append("</th><td>").Append(H(LogArchive.MinuteText(a.SpanFrom) + " → " + LogArchive.MinuteText(a.SpanTo))).Append("</td></tr>");
            }
            if (o.FiltersText.Length > 0) sb.Append("<tr><th>").Append(H(Loc.T("Filtres"))).Append("</th><td>").Append(H(o.FiltersText)).Append("</td></tr>");
            sb.Append("</table></section>");

            // Frise
            if (a != null)
            {
                var src = o.From != int.MinValue ? a.Range(o.From, o.To) : (IList<FileLogEntry>)a.Entries;
                var buckets = a.Timeline(src, 120);
                if (buckets.Count > 0) sb.Append("<section><h2>").Append(H(Loc.T("Frise"))).Append("</h2>").Append(Svg(buckets)).Append("</section>");
            }

            // Constats
            sb.Append("<section><h2>").Append(H(Loc.T("Constats"))).Append("</h2>");
            if (findings.Count == 0) sb.Append("<p class=\"muted\">").Append(H(Loc.T("Aucun problème repéré."))).Append("</p>");
            foreach (var f in findings)
            {
                sb.Append("<article class=\"finding ").Append(f.SeverityKey).Append("\"><div class=\"fh\"><span class=\"ico\">").Append(f.SeverityIcon).Append("</span><strong>")
                  .Append(H(f.Title)).Append("</strong>");
                if (f.Count > 1) sb.Append("<span class=\"count\">").Append(H(f.CountText)).Append("</span>");
                sb.Append("</div>");
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(f.Category)) meta.Add(f.Category);
                if (!string.IsNullOrEmpty(f.App)) meta.Add(f.App);
                if (f.TimeText.Length > 0) meta.Add(f.TimeText);
                sb.Append("<div class=\"meta\">").Append(H(string.Join(" · ", meta))).Append("</div>");
                if (!string.IsNullOrEmpty(f.Detail)) sb.Append("<p>").Append(H(f.Detail).Replace("\n", "<br>")).Append("</p>");
                if (!string.IsNullOrEmpty(f.Advice)) sb.Append("<p class=\"advice\">").Append(H(f.Advice)).Append("</p>");
                if (a != null && f.FirstEntry is FileLogEntry fe)
                {
                    sb.Append("<pre>");
                    foreach (var x in a.Around(fe.Index, 6, 6)) Line(sb, x, x == fe);
                    sb.Append("</pre>");
                }
                sb.Append("</article>");
            }
            sb.Append("</section>");

            // Signets du technicien
            if (o.Bookmarks != null && o.Bookmarks.Count > 0)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Signets du technicien"))).Append("</h2>");
                foreach (var b in o.Bookmarks)
                {
                    sb.Append("<article class=\"finding info\"><div class=\"fh\"><span class=\"ico\">🔖</span><strong>").Append(H(b.Note.Length > 0 ? b.Note : Loc.T("(sans note)")))
                      .Append("</strong><span class=\"count\">").Append(H(b.ShortTime)).Append("</span></div>");
                    if (a != null && b.Entry != null)
                    {
                        sb.Append("<pre>");
                        foreach (var x in a.Around(b.Entry.Index, 4, 4)) Line(sb, x, x == b.Entry);
                        sb.Append("</pre>");
                    }
                    sb.Append("</article>");
                }
                sb.Append("</section>");
            }

            // Avant l'incident
            if (o.Before != null && o.Before.Count > 0)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Avant l'incident"))).Append("</h2><p class=\"muted\">").Append(H(o.BeforeTitle)).Append("</p>")
                  .Append("<table class=\"grid\"><tr><th></th><th>").Append(H(Loc.T("Heure"))).Append("</th><th>").Append(H(Loc.T("Tag"))).Append("</th><th>")
                  .Append(H(Loc.T("Message"))).Append("</th><th>").Append(H(Loc.T("Détail"))).Append("</th></tr>");
                foreach (var r in o.Before)
                    sb.Append("<tr><td>").Append(r.Icon).Append(' ').Append(H(r.KindText)).Append("</td><td>").Append(H(r.ShortTime)).Append("</td><td>").Append(H(r.Tag))
                      .Append("</td><td class=\"mono\">").Append(H(r.Text.Length > 220 ? r.Text.Substring(0, 220) + "…" : r.Text)).Append("</td><td>").Append(H(r.Detail)).Append("</td></tr>");
                sb.Append("</table></section>");
            }

            // Vie de l'appli métier
            if (o.App != null && o.App.Events.Count > 0)
            {
                sb.Append("<section><h2>").Append(H(Loc.F("Vie de l'application {0}", o.App.Package))).Append("</h2><p>").Append(H(o.App.Summary)).Append("</p>")
                  .Append("<table class=\"grid\"><tr><th></th><th>").Append(H(Loc.T("Heure"))).Append("</th><th>").Append(H(Loc.T("Événement"))).Append("</th><th>").Append(H(Loc.T("Détail"))).Append("</th></tr>");
                foreach (var ev in o.App.Events.Take(400))
                    sb.Append("<tr><td>").Append(ev.Icon).Append("</td><td>").Append(H(ev.ShortTime)).Append("</td><td>").Append(H(ev.Text)).Append("</td><td class=\"mono\">").Append(H(ev.Detail)).Append("</td></tr>");
                sb.Append("</table></section>");
            }

            // Sessions
            if (o.Sessions != null && o.Sessions.Count > 1)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Redémarrages et sessions"))).Append("</h2><table class=\"grid\"><tr><th>#</th><th>").Append(H(Loc.T("Début"))).Append("</th><th>")
                  .Append(H(Loc.T("Fin"))).Append("</th><th>").Append(H(Loc.T("Durée"))).Append("</th><th>").Append(H(Loc.T("Fin de session"))).Append("</th></tr>");
                foreach (var s in o.Sessions)
                    sb.Append("<tr><td>").Append(s.Icon).Append(' ').Append(s.Number).Append("</td><td>").Append(H(s.Start)).Append("</td><td>").Append(H(s.End)).Append("</td><td>")
                      .Append(H(s.Duration)).Append("</td><td>").Append(H(s.EndCause)).Append("</td></tr>");
                sb.Append("</table></section>");
            }

            // Comparaison
            if (o.Compare != null && o.Compare.Count > 0)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Comparaison de deux relevés"))).Append("</h2><p class=\"muted\">").Append(H(o.CompareText)).Append("</p>")
                  .Append("<table class=\"grid\"><tr><th>").Append(H(Loc.T("Écart"))).Append("</th><th>A /h</th><th>B /h</th><th>").Append(H(Loc.T("Tag"))).Append("</th><th>")
                  .Append(H(Loc.T("Message"))).Append("</th></tr>");
                foreach (var r in o.Compare.Take(200))
                    sb.Append("<tr><td>").Append(H(r.Verdict)).Append("</td><td>").Append(H(r.RateAText)).Append("</td><td>").Append(H(r.RateBText))
                      .Append("</td><td>").Append(H(r.Tag)).Append("</td><td class=\"mono\">").Append(H(r.Template)).Append("</td></tr>");
                sb.Append("</table></section>");
            }

            // Lignes affichées
            if (o.View != null)
            {
                sb.Append("<section><h2>").Append(H(Loc.T("Lignes affichées"))).Append("</h2>");
                if (o.View.Count <= o.MaxLines)
                {
                    sb.Append("<pre class=\"lines\">");
                    foreach (var x in o.View) Line(sb, x, false);
                    sb.Append("</pre>");
                }
                else
                    sb.Append("<p class=\"muted\">").Append(H(Loc.F("{0} lignes : trop pour ce rapport. Réduisez la période ou ajoutez un filtre, ou exportez les lignes en .txt / .csv.", o.View.Count.ToString("N0", CultureInfo.CurrentCulture)))).Append("</p>");
                sb.Append("</section>");
            }

            sb.Append("<footer>").Append(H(Loc.T("Rapport généré par ADB Tools Pro — logiciel libre (GPL v3)."))).Append("</footer></main></body></html>");
            return sb.ToString();
        }

        private static void Card(StringBuilder sb, string label, string value, string kind = "")
        {
            sb.Append("<div class=\"card ").Append(kind).Append("\"><div class=\"label\">").Append(H(label)).Append("</div><div class=\"value\">").Append(H(value)).Append("</div></div>");
        }

        private static void Line(StringBuilder sb, FileLogEntry x, bool highlight)
        {
            string cls = highlight ? "hl" : x.Level == "E" || x.Level == "F" ? "e" : x.Level == "W" ? "w" : "";
            if (x.Tag == LogArchive.IncidentTag) cls = "inc";
            sb.Append(cls.Length > 0 ? "<span class=\"" + cls + "\">" : "<span>")
              .Append(H($"{x.Time} {x.PID,6} {x.Level} {x.Tag}: {x.Message}"))
              .Append("</span>\n");
        }

        private static string Svg(List<LogArchive.TimelineBucket> b)
        {
            const int w = 960, h = 90;
            double bw = (double)w / b.Count;
            int maxL = Math.Max(1, b.Max(x => x.Lines)), maxE = Math.Max(1, b.Max(x => x.Errors));
            var sb = new StringBuilder();
            sb.Append("<svg class=\"tl\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h + 16).Append("\" role=\"img\">");
            for (int i = 0; i < b.Count; i++)
            {
                double x = i * bw;
                if (b[i].Lines > 0)
                {
                    double lh = Math.Max(1, h * Math.Sqrt(b[i].Lines / (double)maxL));
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"{2:0.#}\" height=\"{3:0.#}\" class=\"l\"/>", x, h - lh, Math.Max(1, bw - 1), lh);
                }
                if (b[i].Errors > 0)
                {
                    double eh = Math.Max(2, h * 0.85 * Math.Sqrt(b[i].Errors / (double)maxE));
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"{2:0.#}\" height=\"{3:0.#}\" class=\"e\"/>", x, h - eh, Math.Max(1, bw - 1), eh);
                }
                if (b[i].Incidents > 0)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "<line x1=\"{0:0.#}\" x2=\"{0:0.#}\" y1=\"0\" y2=\"{1}\" class=\"i\"/>", x + bw / 2, h);
            }
            sb.Append("<text x=\"0\" y=\"").Append(h + 13).Append("\">").Append(H(LogArchive.MinuteText(b[0].From))).Append("</text>");
            sb.Append("<text x=\"").Append(w).Append("\" y=\"").Append(h + 13).Append("\" text-anchor=\"end\">").Append(H(LogArchive.MinuteText(b[b.Count - 1].To))).Append("</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }

        private const string Css = @"
:root{--ink:#1d2327;--muted:#5f6b72;--line:#d9dee2;--brand:#1F7454;--bg:#f6f8f7;--card:#fff;--ok:#1d7a4f;--okbg:#e6f4ec;--warn:#7a5b00;--warnbg:#fbf0c8;--bad:#b3261e;--badbg:#fbe9e7}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 'Segoe UI',system-ui,sans-serif}
main{max-width:1040px;margin:0 auto;padding:28px 20px 40px}
header{border-bottom:3px solid var(--brand);padding-bottom:12px;margin-bottom:18px}.brand{color:var(--brand);font-weight:700;letter-spacing:.02em}
h1{font-size:26px;margin:4px 0 2px}h2{font-size:17px;margin:26px 0 10px;color:var(--brand)}.sub,.muted{color:var(--muted)}
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 14px}.card .label{font-size:12px;color:var(--muted)}.card .value{font-size:18px;font-weight:600}
.card.bad .value{color:var(--bad)}.card.warn .value{color:var(--warn)}.card.ok .value{color:var(--ok)}
table{border-collapse:collapse;width:100%;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden}
th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}.kv th{width:210px;color:var(--muted);font-weight:500}
.grid th{background:#eef2f0;font-size:12px}.mono,pre{font-family:Consolas,'Cascadia Mono',monospace}
.finding{background:var(--card);border:1px solid var(--line);border-left:5px solid var(--muted);border-radius:10px;padding:10px 14px;margin:0 0 10px;break-inside:avoid}
.finding.bad{border-left-color:var(--bad)}.finding.warn{border-left-color:#c79a00}.finding.info{border-left-color:#3b7bbf}
.fh{display:flex;gap:8px;align-items:baseline}.fh .count{margin-left:auto;color:var(--muted);font-size:12px}.meta{color:var(--muted);font-size:12px}
.finding p{margin:6px 0}.advice{font-style:italic;color:var(--muted)}
pre{background:#14181b;color:#d8dee2;border-radius:8px;padding:8px 10px;font-size:11px;overflow-x:auto;white-space:pre;margin:8px 0 0}
pre .e{color:#ff8a80}pre .w{color:#f5d57e}pre .hl{background:#1f7454;color:#fff;display:inline-block;min-width:100%}pre .inc{background:#3a2e0c;color:#f5d57e;font-weight:700}
pre.lines{max-height:none}
.tl{width:100%;height:auto;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:6px}.tl .l{fill:#73d9a9;opacity:.7}.tl .e{fill:#d9483b}.tl .i{stroke:#c79a00;stroke-width:2}.tl text{font-size:10px;fill:var(--muted)}
footer{margin-top:30px;color:var(--muted);font-size:12px;border-top:1px solid var(--line);padding-top:10px}
@media print{body{background:#fff}main{max-width:none;padding:0}pre{white-space:pre-wrap;word-break:break-all}.finding,table{break-inside:avoid}}
";
    }
}
