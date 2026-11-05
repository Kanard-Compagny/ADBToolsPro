using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Remplit le modèle Word du rapport Wi-Fi (Docs/Modeles/Rapport_Wifi.docx ou modèle perso, voir ReportTemplates) avec les données d'une session Radar
    /// Wi-Fi : config réseau, verdict/statistiques, graphiques Ping et RSSI, journal brut des événements
    /// (roaming/déconnexions). Réutilise les utilitaires OpenXML déjà écrits pour le rapport SAV
    /// (WordReportService) plutôt que de les dupliquer.
    /// </summary>
    public static class WifiReportService
    {
        public static string GenerateReport(
            string templatePath, string outputFolder, string serial, string model,
            string societe, string incidentNumber,
            string ssid, string bssid, string freqBand, string linkSpeed, string mac, string ip, string gateway, string dns,
            RadarStats stats, TimeSpan duration,
            string pingChartPath, string rssiChartPath, string rawLogText)
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Template Word introuvable.", templatePath);

            Directory.CreateDirectory(outputFolder);
            string outputPath = Path.Combine(outputFolder, $"Rapport_Wifi_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
            File.Copy(templatePath, outputPath, false);

            using (var wdoc = WordprocessingDocument.Open(outputPath, true))
            {
                if (ReportTemplateEngine.UsesTags(wdoc))
                {
                    // Modèle à balises : mise en page libre ({{SSID}}, {{GRAPHIQUE_PING}}...).
                    var texts = WordReportService.CommonTags(societe, incidentNumber, serial, model);
                    texts["SSID"] = ssid ?? "";
                    texts["BSSID"] = bssid ?? "";
                    texts["FREQUENCE"] = freqBand ?? "";
                    texts["DEBIT"] = linkSpeed ?? "";
                    texts["MAC"] = mac ?? "";
                    texts["IP"] = ip ?? "";
                    texts["PASSERELLE"] = gateway ?? "";
                    texts["DNS"] = dns ?? "";
                    texts["RESUME"] = BuildSummaryText(stats, duration);
                    texts["VERDICT"] = RadarStats.VerdictLabel(stats.Verdict);
                    texts["DUREE"] = RadarStats.FormatDuration(duration);
                    texts["STATS_PING"] = BuildPingStats(stats);
                    texts["STATS_RSSI"] = BuildRssiStats(stats);
                    texts["JOURNAL"] = string.IsNullOrWhiteSpace(rawLogText) ? "Aucun événement réseau brut enregistré durant la session." : rawLogText;

                    var images = new Dictionary<string, TemplateImage>();
                    WordReportService.AddLogo(images);
                    images["GRAPHIQUE_PING"] = new TemplateImage { Path = pingChartPath, FallbackText = "Aucune donnée de latence enregistrée durant la session." };
                    images["GRAPHIQUE_RSSI"] = new TemplateImage { Path = rssiChartPath, FallbackText = "Aucune donnée de signal enregistrée durant la session." };

                    ReportTemplateEngine.Fill(wdoc, texts, images, null, new List<string> { "JOURNAL" });
                }
                else
                {
                    // Ancien modèle sans balises : remplissage par position des tableaux, comme avant.
                    var body = wdoc.MainDocumentPart.Document.Body;
                    var tables = body.Elements<Table>().ToList();
                    if (tables.Count < 4) throw new InvalidOperationException("Structure du template Word Wi-Fi inattendue (tableaux manquants).");

                    FillHeaderTable(tables[0], societe, incidentNumber, serial, model);
                    FillConfigTable(tables[1], ssid, bssid, freqBand, linkSpeed, mac, ip, gateway, dns);
                    FillPingChartTable(wdoc, tables[2], pingChartPath, stats);
                    FillRssiChartTable(wdoc, tables[3], rssiChartPath, stats);
                    FillSummary(body, stats, duration);
                    FillLog(body, rawLogText);
                }

                wdoc.MainDocumentPart.Document.Save();
            }

            // Les graphiques sont désormais embarqués dans le .docx : les PNG temporaires n'ont plus
            // d'utilité et peuvent être supprimés pour ne pas encombrer le dossier de captures.
            foreach (var path in new[] { pingChartPath, rssiChartPath })
            {
                if (string.IsNullOrEmpty(path)) continue;
                try { if (File.Exists(path)) File.Delete(path); }
                catch { /* Non bloquant : au pire le fichier reste, le rapport est déjà généré avec succès. */ }
            }

            return outputPath;
        }

        private static void FillHeaderTable(Table headerTable, string societe, string incidentNumber, string serial, string model)
        {
            var row = headerTable.Elements<TableRow>().FirstOrDefault();
            var cells = row?.Elements<TableCell>().ToList();
            if (cells == null || cells.Count < 4) return;

            WordReportService.FillCommonHeaderCells(cells, societe, incidentNumber, serial, model);
        }

        /// <summary>Remplit la colonne "Valeur" du tableau CONFIGURATION RÉSEAU, ligne par ligne. Les
        /// libellés ("Réseau (SSID)", "Point d'accès (BSSID)"...) sont déjà pré-remplis dans le template ;
        /// "values" doit donc être fourni exactement dans cet ordre : ssid, bssid, freqBand, linkSpeed, mac,
        /// ip, gateway, dns.</summary>
        private static void FillConfigTable(Table configTable, params string[] values)
        {
            var dataRows = configTable.Elements<TableRow>().Skip(1).ToList();
            for (int i = 0; i < dataRows.Count && i < values.Length; i++)
            {
                var cells = dataRows[i].Elements<TableCell>().ToList();
                if (cells.Count < 2) continue;
                WordReportService.SetCellText(cells[1], values[i]);
            }
        }

        /// <summary>Chaque tableau graphique a 3 lignes : titre (déjà rempli dans le template), image
        /// (ligne 2), statistiques détaillées (ligne 3) — reprend les mêmes calculs min/moy/max que
        /// l'export texte existant (BtnExportRadarReport_Click), avec les mêmes sentinelles "--".</summary>
        private static void FillPingChartTable(WordprocessingDocument wdoc, Table table, string chartPath, RadarStats stats)
        {
            var rows = table.Elements<TableRow>().ToList();
            if (rows.Count < 3) return;

            WordReportService.InsertImageOrPlaceholder(wdoc, rows[1].Elements<TableCell>().First(), chartPath, "Aucune donnée de latence enregistrée durant la session.");

            string statsText = BuildPingStats(stats);
            WordReportService.SetCellText(rows[2].Elements<TableCell>().First(), statsText);
        }

        private static void FillRssiChartTable(WordprocessingDocument wdoc, Table table, string chartPath, RadarStats stats)
        {
            var rows = table.Elements<TableRow>().ToList();
            if (rows.Count < 3) return;

            WordReportService.InsertImageOrPlaceholder(wdoc, rows[1].Elements<TableCell>().First(), chartPath, "Aucune donnée de signal enregistrée durant la session.");

            string statsText = BuildRssiStats(stats);
            WordReportService.SetCellText(rows[2].Elements<TableCell>().First(), statsText);
        }

        /// <summary>Résumé/verdict en tête de rapport, même logique de seuils que l'export texte existant
        /// (BtnExportRadarReport_Click) : EXCELLENT / MOYEN / CRITIQUE selon pertes de paquets et déconnexions.</summary>
        private static void FillSummary(Body body, RadarStats stats, TimeSpan duration)
        {
            WordReportService.ReplacePlaceholderParagraphText(body, "[[RESUME]]", BuildSummaryText(stats, duration));
        }

        internal static string BuildSummaryText(RadarStats stats, TimeSpan duration)
        {
            string verdict = RadarStats.VerdictLabel(stats.Verdict);
            return $"Résumé : {verdict} — Durée du test : {RadarStats.FormatDuration(duration)}. " +
                $"RSSI moyen {stats.AverageRssi} dBm, latence moyenne {stats.AveragePingMs} ms, {stats.LossPercent}% de paquets perdus, " +
                $"{stats.RoamingCount} changement(s) d'antenne, {stats.DisconnectCount} déconnexion(s).";
        }

        private static string BuildPingStats(RadarStats stats)
        {
            return $"Paquets envoyés : {stats.PingTotal} — Reçus : {stats.PingReceived} — Perdus : {stats.PingLost} ({stats.LossPercent} %)\n" +
                $"Latence : Min {stats.MinPingText} ms — Moyenne {stats.AveragePingMs} ms — Max {stats.MaxPingText} ms";
        }

        private static string BuildRssiStats(RadarStats stats)
        {
            return $"Signal : Min {stats.MinRssiText} dBm — Moyenne {stats.AverageRssi} dBm — Max {stats.MaxRssiText} dBm";
        }

        /// <summary>Remplace le paragraphe repère "[[LOG]]" par le journal brut complet (police Courier New,
        /// une ligne par w:br), pour prouver au client ce qui a été observé pendant la session.</summary>
        private static void FillLog(Body body, string rawLogText)
        {
            var placeholderPara = body.Elements<Paragraph>().FirstOrDefault(p => p.InnerText.Contains("[[LOG]]"));
            if (placeholderPara == null) return;

            placeholderPara.RemoveAllChildren<Run>();

            string text = string.IsNullOrWhiteSpace(rawLogText) ? "Aucun événement réseau brut enregistré durant la session." : rawLogText.Replace("\r", "");
            string[] lines = text.Split('\n');

            var run = new Run();
            var rPr = new RunProperties(new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" }, new FontSize { Val = "16" });
            run.Append(rPr);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            placeholderPara.Append(run);
        }
    }
}
