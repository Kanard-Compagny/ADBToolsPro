using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Rapport Word « client » d'une analyse de logs, sur le modèle des rapports SAV et Wi-Fi (Docs/Modeles/Rapport_Logs.docx
    /// ou modèle personnel) : même en-tête, logo et pied de page. Les constats forment un tableau ; seuls de courts extraits
    /// du journal y figurent. Le détail complet reste dans le rapport technique (HTML / PDF, LogReport).
    /// </summary>
    public static class LogWordReport
    {
        private const string ConstatsMarker = "§§CONSTATS§§";
        public const int MaxFindings = 30;

        private static string Dev(LogArchive a, string key) => a?.Device.FirstOrDefault(p => p.Key == Loc.T(key)).Value ?? "";

        public static string Generate(string templatePath, string outputFolder, string societe, string incident, LogReport.Options o, string timelinePng)
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Template Word introuvable.", templatePath);
            var a = o.Archive;
            var findings = (o.Analysis?.Findings ?? new List<LogFinding>()).ToList();
            string serial = Dev(a, "N° de série");
            string model = Dev(a, "Modèle");
            if (model.Length == 0) model = Dev(a, "Terminal");
            string fab = Dev(a, "Fabricant");
            if (fab.Length > 0 && !model.StartsWith(fab, StringComparison.OrdinalIgnoreCase)) model = fab + " " + model;

            Directory.CreateDirectory(outputFolder);
            string name = serial.Length > 0 ? serial : Path.GetFileNameWithoutExtension(a?.Title ?? "releve");
            string outputPath = Path.Combine(outputFolder, $"Rapport_Logs_{FileNameHelper.Sanitize(name)}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
            File.Copy(templatePath, outputPath, false);

            int bad = findings.Count(f => f.Severity == FindingSeverity.Bad), warn = findings.Count(f => f.Severity == FindingSeverity.Warn);
            int incidents = findings.Count(f => f.Category == LogAnalyzer.CatIncident);

            var texts = WordReportService.CommonTags(societe, incident, serial, model);
            texts["ANDROID"] = Dev(a, "Version Android");
            texts["BUILD"] = Dev(a, "Build");
            texts["RELEVE"] = a == null ? "" : a.Title + " (" + string.Join(", ", a.LogFiles.Where(f => f.Included).Select(f => f.Name)) + ")";
            texts["PERIODE"] = o.ScopeText ?? "";
            texts["LIGNES"] = (o.Analysis?.LinesAnalyzed ?? 0).ToString("N0", CultureInfo.CurrentCulture);
            texts["NB_PROBLEMES"] = bad.ToString(CultureInfo.CurrentCulture);
            texts["NB_SURVEILLER"] = warn.ToString(CultureInfo.CurrentCulture);
            texts["NB_INCIDENTS"] = incidents.ToString(CultureInfo.CurrentCulture);
            texts["RESUME"] = Summary(o, findings, bad, warn, incidents);
            texts["STATS_FRISE"] = TimelineStats(o);
            texts["CONSTATS"] = findings.Count == 0 ? Loc.T("Aucun problème repéré sur la période analysée.") : ConstatsMarker;
            texts["AVANT_INCIDENT"] = BeforeText(o);
            texts["SESSIONS"] = SessionsText(o);
            texts["SIGNETS"] = BookmarksText(o);
            texts["EXTRAITS"] = ExcerptsText(o, findings);

            var images = new Dictionary<string, TemplateImage>();
            WordReportService.AddLogo(images);
            images["GRAPHIQUE_FRISE"] = new TemplateImage { Path = timelinePng, FallbackText = Loc.T("Pas de frise : aucune ligne datée sur la période.") };

            using (var wdoc = WordprocessingDocument.Open(outputPath, true))
            {
                ReportTemplateEngine.Fill(wdoc, texts, images, null, new List<string> { "EXTRAITS" });
                if (findings.Count > 0) InsertFindingsTable(wdoc, findings);
                wdoc.MainDocumentPart.Document.Save();
            }
            try { if (!string.IsNullOrEmpty(timelinePng) && File.Exists(timelinePng)) File.Delete(timelinePng); } catch { }
            return outputPath;
        }

        // ------------------------------------------------------------------ Textes

        private static string Summary(LogReport.Options o, List<LogFinding> findings, int bad, int warn, int incidents)
        {
            var sb = new StringBuilder();
            sb.Append(Loc.F("Analyse des journaux du terminal sur la période {0} ({1} lignes).", o.ScopeText, (o.Analysis?.LinesAnalyzed ?? 0).ToString("N0", CultureInfo.CurrentCulture))).Append(' ');
            if (findings.Count == 0) sb.Append(Loc.T("Aucun problème n'a été repéré."));
            else
            {
                sb.Append(Loc.F("{0} problème(s) important(s) et {1} point(s) à surveiller ont été repérés.", bad, warn));
                var top = findings.Where(f => f.Severity == FindingSeverity.Bad).Take(3).Select(f => f.Title.TrimStart('⭐', ' ')).ToList();
                if (top.Count > 0) sb.Append(' ').Append(Loc.F("Principaux constats : {0}.", string.Join(" ; ", top)));
            }
            if (incidents > 0) sb.Append(' ').Append(Loc.F("{0} incident(s) ont été signalé(s) par l'utilisateur depuis le terminal.", incidents));
            int unexpected = o.Sessions?.Count(s => s.Unexpected) ?? 0;
            if (unexpected > 0) sb.Append(' ').Append(Loc.F("Le terminal a redémarré de façon inattendue {0} fois.", unexpected));
            return sb.ToString();
        }

        private static string TimelineStats(LogReport.Options o)
        {
            var a = o.Archive;
            if (a == null) return "";
            var src = o.From != int.MinValue ? a.Range(o.From, o.To) : (IList<FileLogEntry>)a.Entries;
            var counts = new int[LogInsights.LaneCount];
            foreach (var e in src) if (e.Lane >= 0) counts[e.Lane]++;
            var names = LogInsights.LaneNames;
            var parts = new List<string>();
            for (int i = 0; i < counts.Length; i++) if (counts[i] > 0) parts.Add(names[i] + " : " + counts[i]);
            return parts.Count == 0 ? Loc.T("Aucun redémarrage, plantage, coupure Wi-Fi, manque de mémoire ni défaut du lecteur sur la période.")
                                    : Loc.T("Événements sur la période") + " — " + string.Join(" · ", parts);
        }

        private static string BeforeText(LogReport.Options o)
        {
            if (o.Before == null || o.Before.Count == 0) return Loc.T("Aucun incident n'a été examiné en détail.");
            var sb = new StringBuilder();
            sb.AppendLine(o.BeforeTitle);
            foreach (var r in o.Before.Take(12))
            {
                string msg = r.Text.Length > 140 ? r.Text.Substring(0, 140) + "…" : r.Text;
                sb.AppendLine($"• {r.ShortTime} — {r.KindText} — {r.Tag} : {msg}" + (r.Detail.Length > 0 ? $" ({r.Detail})" : ""));
            }
            if (o.Before.Count > 12) sb.AppendLine(Loc.F("… et {0} autre(s) élément(s) dans le rapport technique.", o.Before.Count - 12));
            return sb.ToString().TrimEnd();
        }

        private static string SessionsText(LogReport.Options o)
        {
            var sb = new StringBuilder();
            if (o.Sessions != null && o.Sessions.Count > 1)
            {
                foreach (var s in o.Sessions)
                    sb.AppendLine(Loc.F("• Session {0} : {1} → {2} ({3}) — {4}", s.Number, s.Start, s.End, s.Duration, s.EndCause));
            }
            else sb.AppendLine(Loc.T("Aucun redémarrage du terminal dans le relevé."));
            if (o.App != null && o.App.Events.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Loc.F("Application {0} : {1}", o.App.Package, o.App.Summary));
                foreach (var ev in o.App.Events.Where(x => x.Kind != "start" && x.Kind != "died").Take(10))
                    sb.AppendLine($"• {ev.ShortTime} — {ev.Text}" + (ev.Detail.Length > 0 && ev.Detail.Length < 120 ? " (" + ev.Detail + ")" : ""));
            }
            return sb.ToString().TrimEnd();
        }

        private static string BookmarksText(LogReport.Options o)
        {
            if (o.Bookmarks == null || o.Bookmarks.Count == 0) return Loc.T("Aucune observation particulière.");
            var sb = new StringBuilder();
            foreach (var b in o.Bookmarks)
            {
                sb.AppendLine($"• {b.ShortTime} — {(b.Note.Length > 0 ? b.Note : Loc.T("(sans note)"))}");
                string m = b.Message ?? "";
                sb.AppendLine($"   {b.Tag} : {(m.Length > 140 ? m.Substring(0, 140) + "…" : m)}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Quelques lignes autour des principaux constats (le rapport technique contient tout).</summary>
        private static string ExcerptsText(LogReport.Options o, List<LogFinding> findings)
        {
            var a = o.Archive;
            var picked = findings.Where(f => f.FirstEntry is FileLogEntry).OrderByDescending(f => f.Severity).Take(6).ToList();
            if (a == null || picked.Count == 0) return Loc.T("Aucun extrait.");
            var sb = new StringBuilder();
            foreach (var f in picked)
            {
                var fe = (FileLogEntry)f.FirstEntry;
                sb.AppendLine("▶ " + f.Title.TrimStart('⭐', ' '));
                foreach (var x in a.Around(fe.Index, 2, 2))
                {
                    string line = $"{(x.Time.Length > 6 ? x.Time.Substring(6) : x.Time)} {x.Level} {x.Tag}: {x.Message}";
                    if (line.Length > 150) line = line.Substring(0, 150) + "…";
                    sb.AppendLine((x == fe ? "» " : "  ") + line);
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        // ------------------------------------------------------------------ Tableau des constats

        /// <summary>
        /// Remplace le paragraphe {{CONSTATS}} par un tableau, mis en forme comme le tableau « Paramètre / Valeur » du modèle
        /// (copie de sa ligne d'en-tête et d'une ligne de données).
        /// </summary>
        private static void InsertFindingsTable(WordprocessingDocument wdoc, List<LogFinding> findings)
        {
            var body = wdoc.MainDocumentPart.Document.Body;
            var marker = body.Descendants<Paragraph>().FirstOrDefault(p => p.InnerText.Contains(ConstatsMarker));
            if (marker == null) return;
            bool TwoCols(Table t) => t.Elements<TableRow>().Count() >= 2 && t.Elements<TableRow>().First().Elements<TableCell>().Count() == 2;
            var model = body.Descendants<Table>().FirstOrDefault(t => TwoCols(t) && (t.Elements<TableRow>().First().InnerText.Contains(Loc.T("Paramètre")) || t.Elements<TableRow>().First().InnerText.Contains("Paramètre") || t.Elements<TableRow>().First().InnerText.Contains("Parameter")))
                        ?? body.Descendants<Table>().FirstOrDefault(TwoCols);
            if (model == null)
            {
                // Modèle personnel sans tableau à deux colonnes : liste simple.
                var text = string.Join("\n", findings.Take(MaxFindings).Select(f => $"{f.SeverityIcon} {f.Title} — {f.Detail}"));
                SetParagraphLines(marker, text.Split('\n'), false);
                return;
            }
            var table = (Table)model.CloneNode(true);
            var rows = table.Elements<TableRow>().ToList();
            var header = rows[0];
            var template = (TableRow)rows[1].CloneNode(true);
            foreach (var r in rows.Skip(1)) r.Remove();
            var hc = header.Elements<TableCell>().ToList();
            SetCellLines(hc[0], new[] { Loc.T("Constat") }, false);
            SetCellLines(hc[1], new[] { Loc.T("Détail et conseil") }, false);

            foreach (var f in findings.Take(MaxFindings))
            {
                var row = (TableRow)template.CloneNode(true);
                var cells = row.Elements<TableCell>().ToList();
                var left = new List<string> { f.SeverityIcon + " " + f.Title.TrimStart('⭐', ' ') };
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(f.App)) meta.Add(f.App);
                if (f.TimeText.Length > 0) meta.Add(f.TimeText);
                if (f.Count > 1) meta.Add(f.CountText);
                if (meta.Count > 0) left.Add(string.Join(" · ", meta));
                if (!string.IsNullOrEmpty(f.TrendText)) left.Add(f.TrendText);
                SetCellLines(cells[0], left.ToArray(), true);
                var right = new List<string>();
                if (!string.IsNullOrEmpty(f.Detail)) right.AddRange(f.Detail.Split('\n').Take(8));
                if (!string.IsNullOrEmpty(f.Advice)) right.Add("→ " + f.Advice);
                SetCellLines(cells[1], right.ToArray(), false);
                WordReportService.ShadeCell(cells[0], f.Severity == FindingSeverity.Bad ? "F8D7DA" : f.Severity == FindingSeverity.Warn ? "FFF3CD" : "E2EEF9");
                table.Append(row);
            }
            if (findings.Count > MaxFindings)
            {
                var row = (TableRow)template.CloneNode(true);
                var cells = row.Elements<TableCell>().ToList();
                SetCellLines(cells[0], new[] { "…" }, false);
                SetCellLines(cells[1], new[] { Loc.F("{0} autre(s) constat(s) dans le rapport technique.", findings.Count - MaxFindings) }, false);
                table.Append(row);
            }
            marker.InsertBeforeSelf(table);
            marker.Remove();
        }

        /// <summary>Texte d'une cellule sur plusieurs lignes, en gardant la mise en forme de son premier paragraphe.</summary>
        private static void SetCellLines(TableCell cell, string[] lines, bool firstBold)
        {
            var paras = cell.Elements<Paragraph>().ToList();
            var p = paras.FirstOrDefault() ?? cell.AppendChild(new Paragraph());
            foreach (var extra in paras.Skip(1)) extra.Remove();
            SetParagraphLines(p, lines, firstBold);
        }

        private static void SetParagraphLines(Paragraph p, string[] lines, bool firstBold)
        {
            var rPr = p.Descendants<RunProperties>().FirstOrDefault()?.CloneNode(true) as RunProperties;
            p.RemoveAllChildren<Run>();
            p.RemoveAllChildren<Hyperlink>();
            for (int i = 0; i < lines.Length; i++)
            {
                var run = new Run();
                var props = rPr != null ? (RunProperties)rPr.CloneNode(true) : new RunProperties();
                props.RemoveAllChildren<Bold>();
                if (i == 0 && firstBold) props.Append(new Bold());
                if (props.HasChildren) run.Append(props);
                if (i > 0) run.Append(new Break());
                run.Append(new Text(lines[i] ?? "") { Space = SpaceProcessingModeValues.Preserve });
                p.Append(run);
            }
        }

        // ------------------------------------------------------------------ PDF du rapport technique

        /// <summary>Navigateur capable d'imprimer en PDF sans fenêtre (Edge, présent sur Windows 10 / 11, ou Chrome).</summary>
        public static string FindPdfBrowser()
        {
            var candidates = new List<string>();
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
            }
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Imprime une page HTML en PDF avec le navigateur, sans fenêtre. Renvoie null si tout s'est bien passé, sinon l'erreur.</summary>
        public static string HtmlToPdf(string htmlPath, string pdfPath, int timeoutMs = 90000)
        {
            string browser = FindPdfBrowser();
            if (browser == null) return Loc.T("Ni Microsoft Edge ni Google Chrome n'ont été trouvés sur ce poste.");
            string profile = Path.Combine(Path.GetTempPath(), "ADBTools_pdf_" + Guid.NewGuid().ToString("N"));
            try
            {
                if (File.Exists(pdfPath)) File.Delete(pdfPath);
                string url = new Uri(htmlPath).AbsoluteUri;
                var psi = new ProcessStartInfo(browser,
                    $"--headless --disable-gpu --no-first-run --no-default-browser-check --user-data-dir=\"{profile}\" --no-pdf-header-footer --print-to-pdf-no-header --print-to-pdf=\"{pdfPath}\" \"{url}\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return Loc.T("Le navigateur n'a pas répondu à temps."); }
                }
                return File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0 ? null : Loc.T("Le navigateur n'a pas produit de PDF.");
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { } }
        }
    }
}
