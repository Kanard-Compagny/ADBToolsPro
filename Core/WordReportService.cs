using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace ADBToolsNew.Core
{
    /// <summary>
    /// Remplit le modèle Word du rapport SAV (Docs/Modeles/Rapport_SAV.docx ou modèle perso, voir ReportTemplates) avec les données d'un rapport Diagnostics
    /// SAV : infos terminal, résultats par module et visuels des tests manuels (grille tactile, journal des
    /// touches). Repose uniquement sur le SDK DocumentFormat.OpenXml (portable, aucune dépendance à Word).
    /// Le template original n'est jamais modifié : une copie est créée puis remplie.
    /// </summary>
    public static class WordReportService
    {
        // Ordre des lignes de chaque tableau de modules dans le template, identique à l'ordre de
        // génération utilisé lors de la reconstruction du template. "crashes" est volontairement exclu du
        // rapport client (trop technique) mais reste disponible dans l'export JSON.
        private static readonly string[] AutoModuleOrder = { "wifi", "ping", "battery", "storage", "sensors", "thermal" };
        private static readonly string[] ManualModuleOrder = { "keys", "touch", "scanner", "screen" };

        // Textes d'instruction affichés par défaut dans l'app tant qu'un test manuel n'a jamais été
        // réellement exécuté (voir InitializeDiagChecklist) : ce sont des consignes pour LE TECHNICIEN
        // ("allez dans le Laboratoire..."), pas des résultats, donc ils n'ont rien à faire dans un rapport
        // destiné au client. Comparés tels quels au champ Details pour savoir s'il faut les masquer.
        private static readonly string[] InstructionOnlyDetails =
        {
            "Testez les boutons dans le Laboratoire en bas à droite.",
            "Testez le tactile dans le Laboratoire en bas à droite.",
            "Déclenchez la gâchette dans le Laboratoire (détection automatique du signal) et vérifiez visuellement que le laser/illuminateur s'allume et décode un code-barres réel.",
            "Utilisez les mires de couleur dans le Laboratoire en bas à droite."
        };

        /// <summary>Prépare le champ "Details" pour un rapport destiné au client : masque les textes
        /// d'instruction interne (test jamais réalisé) et retire les notes techniques internes (préfixées
        /// "ℹ️" dans tout le module Diagnostics, ex: fiabilité d'une détection, conseils au technicien).</summary>
        private static string CleanDetailsForClient(string details)
        {
            if (string.IsNullOrWhiteSpace(details)) return "";
            if (InstructionOnlyDetails.Contains(details.Trim())) return "";

            var keptLines = details
                .Replace("\r", "")
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("ℹ️"));

            return string.Join("\n", keptLines).Trim('\n');
        }

        /// <summary>Noms des tests inclus dans le rapport (Auto + Manuel) encore au statut "Non testé"
        /// (⚪), pour permettre d'avertir le technicien avant génération sans bloquer le rapport.</summary>
        public static List<string> GetUntestedComponentNames(IEnumerable<DiagComponent> components)
        {
            var byId = components.Where(c => !string.IsNullOrEmpty(c.SystemId)).ToDictionary(c => c.SystemId, c => c);
            return AutoModuleOrder.Concat(ManualModuleOrder)
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .Where(item => item.StatusIcon == "⚪")
                .Select(item => item.ComponentName)
                .ToList();
        }

        public static string GenerateReport(string templatePath, string outputFolder, string serial, string societe, string incidentNumber, IEnumerable<DiagComponent> components)
        {
            if (!File.Exists(templatePath)) throw new FileNotFoundException("Template Word introuvable.", templatePath);

            Directory.CreateDirectory(outputFolder);
            string outputPath = Path.Combine(outputFolder, $"Rapport_SAV_{FileNameHelper.Sanitize(serial)}_{DateTime.Now:yyyyMMdd_HHmmss}.docx");
            File.Copy(templatePath, outputPath, false);

            var byId = components.Where(c => !string.IsNullOrEmpty(c.SystemId)).ToDictionary(c => c.SystemId, c => c);

            // Identité du terminal déjà collectée par le test "auth" du check-up auto : aucun appel ADB
            // supplémentaire nécessaire, on la relit simplement depuis son texte.
            string authDetails = byId.TryGetValue("auth", out var authItem) ? (authItem.Details ?? "") : "";
            string model = ExtractField(authDetails, "Modèle");
            string androidVersion = ExtractField(authDetails, "Version Android");
            string buildOs = ExtractField(authDetails, "Build OS");

            using (var wdoc = WordprocessingDocument.Open(outputPath, true))
            {
                if (ReportTemplateEngine.UsesTags(wdoc))
                {
                    // Modèle à balises ({{SOCIETE}}, {{WIFI_STATUT}}...) : mise en page libre.
                    FillByTags(wdoc, serial, societe, incidentNumber, model, androidVersion, buildOs, byId);
                }
                else
                {
                    // Ancien modèle sans balises : remplissage par position des tableaux, comme avant.
                    var body = wdoc.MainDocumentPart.Document.Body;
                    var tables = body.Elements<Table>().ToList();
                    if (tables.Count < 4) throw new InvalidOperationException("Structure du template Word inattendue (tableaux manquants).");

                    FillHeaderTable(tables[0], societe, incidentNumber, serial, model, androidVersion, buildOs);
                    FillModuleTable(tables[1], AutoModuleOrder, byId);
                    FillModuleTable(tables[2], ManualModuleOrder, byId);
                    FillVisualsTable(wdoc, tables[3], byId);
                    FillSummary(body, AutoModuleOrder.Concat(ManualModuleOrder), byId);
                }

                wdoc.MainDocumentPart.Document.Save();
            }

            // Les PNG de capture (grille tactile...) ne sont PAS supprimés ici : régénérer le rapport (ex.
            // pour corriger une faute de frappe Société/N° d'incident) doit continuer à retrouver la même
            // capture. Le nettoyage effectif a lieu au moment de "Nouveau test" (BtnResetDiagnostic_Click),
            // quand la session de diagnostic à laquelle ces captures appartiennent est réellement terminée.

            return outputPath;
        }

        // Nom des balises pour chaque test : {{WIFI_DETAILS}}, {{WIFI_STATUT}}, {{WIFI_COMMENTAIRE}}...
        private static readonly Dictionary<string, string> ModuleTagNames = new Dictionary<string, string>
        {
            ["wifi"] = "WIFI", ["ping"] = "PING", ["battery"] = "BATTERIE", ["storage"] = "STOCKAGE",
            ["sensors"] = "CAPTEURS", ["thermal"] = "THERMIQUE",
            ["keys"] = "BOUTONS", ["touch"] = "TACTILE", ["scanner"] = "SCANNER", ["screen"] = "ECRAN"
        };

        /// <summary>Balises communes aux deux rapports (en-tête, pied de page).</summary>
        internal static Dictionary<string, string> CommonTags(string societe, string incidentNumber, string serial, string model)
        {
            return new Dictionary<string, string>
            {
                ["SOCIETE"] = societe ?? "",
                ["MA_SOCIETE"] = AppSettings.Current.ReportCompany?.Trim() ?? "",
                ["COORDONNEES"] = AppSettings.Current.ReportContact?.Trim() ?? "",
                ["TECHNICIEN"] = AppSettings.Current.ReportTechnician?.Trim() ?? "",
                ["INCIDENT"] = incidentNumber ?? "",
                ["NUMERO_SERIE"] = serial ?? "",
                ["MODELE"] = model ?? "",
                ["DATE"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                ["PIED_DE_PAGE"] = "Rapport généré par ADB Tools — " + DateTime.Now.ToString("dd/MM/yyyy")
            };
        }

        /// <summary>Logo personnel (Templates\logo.png) à la place de {{LOGO}} : environ 2,5 cm de haut au maximum.
        /// Sans logo, la balise disparaît simplement.</summary>
        internal static void AddLogo(Dictionary<string, TemplateImage> images)
        {
            images["LOGO"] = new TemplateImage
            {
                Path = ReportTemplates.GetLogoPath(),
                FallbackText = "",
                MaxWidthEmu = 2286000L,  // 6,35 cm
                MaxHeightEmu = 900000L   // ≈ 2,5 cm
            };
        }

        private static void FillByTags(WordprocessingDocument wdoc, string serial, string societe, string incidentNumber,
            string model, string androidVersion, string buildOs, Dictionary<string, DiagComponent> byId)
        {
            var texts = CommonTags(societe, incidentNumber, serial, model);
            texts["VERSION_ANDROID"] = androidVersion ?? "";
            texts["BUILD_OS"] = buildOs ?? "";
            texts["RESUME"] = BuildSummaryFor(AutoModuleOrder.Concat(ManualModuleOrder), byId);

            var fills = new Dictionary<string, string>();
            foreach (var pair in ModuleTagNames)
            {
                byId.TryGetValue(pair.Key, out var item);
                texts[pair.Value + "_DETAILS"] = item == null ? "" : CleanDetailsForClient(item.Details);
                texts[pair.Value + "_STATUT"] = item == null ? "" : ValidityLabel(item.StatusIcon);
                texts[pair.Value + "_COMMENTAIRE"] = item?.Comment ?? "";
                string fill = item == null ? null : StatusFill(item.StatusIcon);
                if (fill != null) fills[pair.Value + "_STATUT"] = fill;
            }

            var images = new Dictionary<string, TemplateImage>();
            AddLogo(images);
            images["IMAGE_TACTILE"] = new TemplateImage
            {
                Path = byId.TryGetValue("touch", out var touchItem) ? touchItem.ImagePath : null,
                FallbackText = "Aucune capture disponible (test manuel non validé)."
            };

            ReportTemplateEngine.Fill(wdoc, texts, images, fills);
        }

        internal static string ExtractField(string text, string label)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var match = Regex.Match(text, Regex.Escape(label) + @"\s*:\s*(.+)");
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private static void FillHeaderTable(Table headerTable, string societe, string incidentNumber, string serial, string model, string androidVersion, string buildOs)
        {
            var row = headerTable.Elements<TableRow>().FirstOrDefault();
            var cells = row?.Elements<TableCell>().ToList();
            if (cells == null || cells.Count < 4) return;

            FillCommonHeaderCells(cells, societe, incidentNumber, serial, model);
            AppendValueToLabel(cells[3], "Version Android", androidVersion);
            AppendValueToLabel(cells[3], "Build OS", buildOs);
        }

        /// <summary>
        /// Champs d'en-tête communs aux rapports SAV et Wi-Fi (Société, N° d'incident, N° de série, Modèle, Date).
        /// Partagé pour que les deux modèles restent remplis de la même façon.
        /// </summary>
        internal static void FillCommonHeaderCells(IList<TableCell> cells, string societe, string incidentNumber, string serial, string model)
        {
            if (cells == null || cells.Count < 4) return;

            AppendValueToLabel(cells[0], "Société", societe);
            AppendValueToLabel(cells[0], "N° d’incident", incidentNumber);
            AppendValueToLabel(cells[0], "N° d'incident", incidentNumber); // apostrophe droite, au cas où

            AppendValueToLabel(cells[3], "N° de série", serial);
            AppendValueToLabel(cells[3], "Modèle", model);
            AppendValueToLabel(cells[3], "Date du diagnostic", DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        }

        /// <summary>
        /// Remplace le texte du paragraphe contenant le repère (ex. "[[RESUME]]") par <paramref name="newText"/>.
        /// Partagé entre les deux rapports. Word découpe parfois un repère sur plusieurs morceaux de texte
        /// ("[[RES" + "UME]]") : le premier reçoit le texte, les suivants sont vidés, pour ne pas laisser
        /// de reste du repère dans le rapport.
        /// </summary>
        internal static void ReplacePlaceholderParagraphText(Body body, string marker, string newText)
        {
            var paragraph = body.Elements<Paragraph>().FirstOrDefault(p => p.InnerText.Contains(marker));
            if (paragraph == null) return;

            var texts = paragraph.Descendants<Text>().ToList();
            if (texts.Count == 0) return;

            texts[0].Text = newText;
            texts[0].Space = SpaceProcessingModeValues.Preserve;
            for (int i = 1; i < texts.Count; i++) texts[i].Text = "";
        }

        /// <summary>Ajoute la valeur juste après le libellé (ex: "Modèle : ") dans le premier paragraphe qui le contient.</summary>
        internal static void AppendValueToLabel(TableCell cell, string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            var para = cell.Elements<Paragraph>().FirstOrDefault(p => p.InnerText.Contains(label));
            if (para == null) return;

            // Le modèle Word contient parfois déjà des espaces après le libellé ("N° d'incident :" suivi d'un
            // run "  ") : on retire les blancs de fin pour n'en remettre qu'un seul, quel que soit le modèle.
            TrimTrailingWhitespace(para);

            var lastRun = para.Elements<Run>().LastOrDefault();
            var newRun = new Run();
            if (lastRun?.RunProperties != null) newRun.Append((RunProperties)lastRun.RunProperties.CloneNode(true));
            newRun.Append(new Text(" " + value.Trim()) { Space = SpaceProcessingModeValues.Preserve });
            para.Append(newRun);
        }

        private static readonly char[] LabelWhitespace = { ' ', '\u00A0', '\u202F', '\t' };

        /// <summary>Supprime les espaces (normaux, insécables, tabulations) en fin de paragraphe, à travers
        /// plusieurs runs si besoin, en s'arrêtant au premier texte qui contient autre chose.</summary>
        internal static void TrimTrailingWhitespace(Paragraph para)
        {
            foreach (var text in para.Descendants<Text>().Reverse().ToList())
            {
                string trimmed = (text.Text ?? "").TrimEnd(LabelWhitespace);
                text.Text = trimmed;
                if (trimmed.Length > 0) break;
            }
        }

        private static void FillModuleTable(Table moduleTable, string[] order, Dictionary<string, DiagComponent> byId)
        {
            // Chaque tableau (Auto ou Manuel) n'a plus qu'une ligne d'en-tête suivie de ses lignes de
            // données : plus de bandeau de section à filtrer (le titre vit désormais dans un paragraphe
            // au-dessus du tableau).
            var dataRows = moduleTable.Elements<TableRow>().Skip(1).ToList();

            for (int i = 0; i < dataRows.Count && i < order.Length; i++)
            {
                if (!byId.TryGetValue(order[i], out var item)) continue;

                var cells = dataRows[i].Elements<TableCell>().ToList();
                if (cells.Count < 4) throw new InvalidOperationException($"Structure du template Word inattendue (ligne \"{order[i]}\" : {cells.Count} cellule(s) au lieu de 4 — le template a-t-il été modifié ?)");

                SetCellText(cells[1], CleanDetailsForClient(item.Details));
                SetValidityCell(cells[2], item.StatusIcon);
                SetCellText(cells[3], item.Comment);
            }
        }

        /// <summary>Résumé rapide en tête de rapport : compte les statuts des tests réellement affichés
        /// dans le rapport (Auto + Manuel), pour une lecture immédiate sans parcourir tout le détail.</summary>
        private static void FillSummary(Body body, IEnumerable<string> order, Dictionary<string, DiagComponent> byId)
        {
            ReplacePlaceholderParagraphText(body, "[[RESUME]]", BuildSummaryFor(order, byId));
        }

        private static string BuildSummaryFor(IEnumerable<string> order, Dictionary<string, DiagComponent> byId)
        {
            var items = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            int conforme = items.Count(i => i.StatusIcon == "✅");
            int surveiller = items.Count(i => i.StatusIcon == "⚠️");
            int nonConforme = items.Count(i => i.StatusIcon == "❌");
            int total = items.Count;

            string summary = BuildSummaryText(
                conforme, surveiller, nonConforme, total,
                nonTeste: items.Count(i => i.StatusIcon == "⚪"),
                nonApplicable: items.Count(i => i.StatusIcon == "➖"),
                deconnecte: items.Count(i => i.StatusIcon == "🔌"));

            return summary;
        }

        /// <summary>Fonction pure, testable. Les tests non réalisés (non testé, non applicable, terminal
        /// déconnecté) sont détaillés à part : auparavant le résumé annonçait "8 conforme(s) ... sur 10 test(s)
        /// réalisé(s)" alors que 2 des 10 n'avaient pas été faits.</summary>
        internal static string BuildSummaryText(int conforme, int surveiller, int nonConforme, int total,
            int nonTeste, int nonApplicable, int deconnecte)
        {
            string summary = $"Résumé : {conforme} conforme(s), {surveiller} à surveiller, {nonConforme} non conforme(s) sur {total} test(s)";

            var details = new List<string>();
            if (nonTeste > 0) details.Add($"{nonTeste} non testé(s)");
            if (nonApplicable > 0) details.Add($"{nonApplicable} non applicable(s)");
            if (deconnecte > 0) details.Add($"{deconnecte} interrompu(s) (terminal déconnecté)");

            if (details.Count > 0) summary += ", dont " + JoinFrench(details);
            return summary + ".";
        }

        private static string JoinFrench(List<string> parts)
        {
            if (parts.Count == 1) return parts[0];
            return string.Join(", ", parts.Take(parts.Count - 1)) + " et " + parts[parts.Count - 1];
        }

        private static string ValidityLabel(string statusIcon)
        {
            switch (statusIcon)
            {
                case "✅": return "Conforme";
                case "❌": return "Non conforme";
                case "⚠️": return "À surveiller";
                case "➖": return "Non applicable";
                case "🔌": return "Terminal déconnecté";
                case "⚪": return "Non testé";
                default: return statusIcon ?? "";
            }
        }

        /// <summary>Remplit la cellule "Validité" avec son libellé et colore son fond selon le statut
        /// (vert/orange/rouge), pour repérer un souci en un coup d'œil dans le tableau.</summary>
        private static void SetValidityCell(TableCell cell, string statusIcon)
        {
            SetCellText(cell, ValidityLabel(statusIcon));
            string fill = StatusFill(statusIcon);
            if (fill == null) return; // Non testé / Non applicable / Terminal déconnecté : fond du template inchangé
            ShadeCell(cell, fill);
        }

        /// <summary>Couleur de fond de la case « Validité » selon le statut, ou null pour laisser celle du modèle.</summary>
        internal static string StatusFill(string statusIcon)
        {
            switch (statusIcon)
            {
                case "✅": return "C6EFCE"; // vert clair
                case "⚠️": return "FFEB9C"; // jaune clair
                case "❌": return "FFC7CE"; // rouge clair
                default: return null;
            }
        }

        /// <summary>Colore le fond d'une cellule de tableau (hexadécimal sans #).</summary>
        internal static void ShadeCell(TableCell cell, string fill)
        {
            var tcPr = cell.TableCellProperties;
            if (tcPr == null) { tcPr = new TableCellProperties(); cell.PrependChild(tcPr); }

            var existingShading = tcPr.Elements<Shading>().FirstOrDefault();
            if (existingShading != null) tcPr.RemoveChild(existingShading);

            // Respecte l'ordre du schéma CT_TcPr (shd juste après tcBorders, avant vAlign/hideMark) pour
            // éviter que Word ne signale le document comme "réparé" à l'ouverture.
            var shading = new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill };
            var tcBorders = tcPr.Elements<TableCellBorders>().FirstOrDefault();
            if (tcBorders != null) tcPr.InsertAfter(shading, tcBorders);
            else tcPr.PrependChild(shading);
        }

        /// <summary>Remplace le contenu texte d'une cellule (vidée puis reconstruite ligne par ligne), en
        /// conservant la police d'origine du paragraphe.</summary>
        internal static void SetCellText(TableCell cell, string text)
        {
            var para = cell.Elements<Paragraph>().FirstOrDefault();
            if (para == null) { para = new Paragraph(); cell.Append(para); }

            var existingRunProps = para.ParagraphProperties?.ParagraphMarkRunProperties;
            para.RemoveAllChildren<Run>();

            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            var run = new Run();
            if (existingRunProps != null) run.Append(new RunProperties(existingRunProps.ChildElements.Select(c => c.CloneNode(true))));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) run.Append(new Break());
                run.Append(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
            }
            para.Append(run);
        }

        private static void FillVisualsTable(WordprocessingDocument wdoc, Table visualsTable, Dictionary<string, DiagComponent> byId)
        {
            var rows = visualsTable.Elements<TableRow>().ToList();
            if (rows.Count < 2) return;

            var cells = rows[1].Elements<TableCell>().ToList();
            if (cells.Count < 1) return;

            InsertImageOrPlaceholder(wdoc, cells[0], byId.TryGetValue("touch", out var touchItem) ? touchItem.ImagePath : null, "Aucune capture disponible (test manuel non validé).");
        }

        internal static void InsertImageOrPlaceholder(WordprocessingDocument wdoc, TableCell cell, string imagePath, string placeholderMessage)
        {
            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                SetCellText(cell, placeholderMessage);
                return;
            }

            var mainPart = wdoc.MainDocumentPart;
            ImagePart imagePart = mainPart.AddImagePart(ImagePartType.Png);
            using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
            {
                imagePart.FeedData(stream);
            }
            string relId = mainPart.GetIdOfPart(imagePart);

            var decoder = BitmapDecoder.Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            int pxW = decoder.Frames[0].PixelWidth;
            int pxH = decoder.Frames[0].PixelHeight;

            const long maxWidthEmu = 6300000L; // ~6.9 in : tient dans la colonne pleine largeur (~7.25 in) avec marge
            long emuW = (long)pxW * 9525L;
            long emuH = (long)pxH * 9525L;
            if (emuW > maxWidthEmu)
            {
                double scale = (double)maxWidthEmu / emuW;
                emuW = maxWidthEmu;
                emuH = (long)(emuH * scale);
            }

            // Identifiant d'image unique dans le document : le rapport Wi-Fi insère 2 graphiques, et deux images
            // avec le même identifiant (auparavant toujours 1) peuvent faire signaler le fichier comme endommagé par Word.
            uint drawingId = 1;
            var existingIds = mainPart.Document.Body.Descendants<DW.DocProperties>()
                .Where(dp => dp.Id != null && dp.Id.HasValue)
                .Select(dp => dp.Id.Value)
                .ToList();
            if (existingIds.Count > 0) drawingId = existingIds.Max() + 1;

            var drawing = new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = emuW, Cy = emuH },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = drawingId, Name = $"Capture {drawingId}" },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = "capture.png" },
                                    new PIC.NonVisualPictureDrawingProperties()),
                                new PIC.BlipFill(
                                    new A.Blip { Embed = relId },
                                    new A.Stretch(new A.FillRectangle())),
                                new PIC.ShapeProperties(
                                    new A.Transform2D(
                                        new A.Offset { X = 0L, Y = 0L },
                                        new A.Extents { Cx = emuW, Cy = emuH }),
                                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
                            )
                        ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })
                )
                { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U });

            var para = cell.Elements<Paragraph>().FirstOrDefault();
            if (para == null) { para = new Paragraph(); cell.Append(para); }
            para.RemoveAllChildren<Run>();
            para.Append(new Run(drawing));
        }
    }
}
