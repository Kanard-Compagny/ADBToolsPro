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
    /// <summary>Une image à insérer à la place d'une balise (ex. {{IMAGE_TACTILE}}), avec un texte de
    /// remplacement si le fichier n'existe pas.</summary>
    public sealed class TemplateImage
    {
        public string Path { get; set; }
        public string FallbackText { get; set; }
        /// <summary>Largeur maximale en EMU (914400 EMU = 1 pouce). 0 = largeur de page par défaut.</summary>
        public long MaxWidthEmu { get; set; }
        /// <summary>Hauteur maximale en EMU. 0 = pas de limite.</summary>
        public long MaxHeightEmu { get; set; }
    }

    /// <summary>
    /// Remplissage des modèles Word par balises : {{SOCIETE}}, {{NUMERO_SERIE}}, {{WIFI_STATUT}}...
    /// Les balises peuvent être placées n'importe où (corps, tableaux, en-tête, pied de page) : chacun peut
    /// donc refaire la mise en page de son modèle sans toucher au code. Une balise inconnue est laissée telle
    /// quelle dans le rapport, pour que l'utilisateur repère une faute de frappe.
    /// </summary>
    public static class ReportTemplateEngine
    {
        private static readonly Regex TagRegex = new Regex(@"\{\{([A-Z0-9_]+)\}\}", RegexOptions.Compiled);

        /// <summary>Vrai si le document contient au moins une balise {{...}} (sinon : ancien modèle sans
        /// balises, rempli par position comme avant).</summary>
        public static bool UsesTags(WordprocessingDocument doc)
        {
            return GetRoots(doc).Any(r => r.Root != null && TagRegex.IsMatch(r.Root.InnerText));
        }

        /// <param name="texts">Balise (sans accolades) → texte. Les retours à la ligne (\n) sont conservés.</param>
        /// <param name="images">Balise → image à insérer.</param>
        /// <param name="cellFills">Balise → couleur de fond (hexadécimal, ex. "C6EFCE") de la cellule de tableau qui la contient.</param>
        /// <param name="monospaceTags">Balises dont le texte est écrit en police à chasse fixe (ex. journal brut).</param>
        public static void Fill(WordprocessingDocument doc,
            IDictionary<string, string> texts,
            IDictionary<string, TemplateImage> images = null,
            IDictionary<string, string> cellFills = null,
            ICollection<string> monospaceTags = null)
        {
            texts = texts ?? new Dictionary<string, string>();
            images = images ?? new Dictionary<string, TemplateImage>();
            cellFills = cellFills ?? new Dictionary<string, string>();
            monospaceTags = monospaceTags ?? new List<string>();

            foreach (var target in GetRoots(doc).ToList())
            {
                if (target.Root == null) continue;
                foreach (var paragraph in target.Root.Descendants<Paragraph>().ToList())
                {
                    string full = paragraph.InnerText;
                    if (string.IsNullOrEmpty(full) || !full.Contains("{{")) continue;

                    var tagsHere = TagRegex.Matches(full).Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                    if (tagsHere.Count == 0) continue;

                    // 1. Couleur de fond de la cellule (ex. Validité : vert / jaune / rouge)
                    foreach (var tag in tagsHere)
                    {
                        if (cellFills.TryGetValue(tag, out var fill) && !string.IsNullOrEmpty(fill))
                        {
                            var cell = paragraph.Ancestors<TableCell>().FirstOrDefault();
                            if (cell != null) WordReportService.ShadeCell(cell, fill);
                        }
                    }

                    // 2. Image : le paragraphe entier est remplacé par l'image (ou le texte de remplacement)
                    string imageTag = tagsHere.FirstOrDefault(t => images.ContainsKey(t));
                    if (imageTag != null)
                    {
                        var img = images[imageTag];
                        if (img != null && !string.IsNullOrEmpty(img.Path) && File.Exists(img.Path))
                        {
                            var drawing = CreateImageDrawing(doc, target.Part, img);
                            if (drawing != null)
                            {
                                paragraph.RemoveAllChildren<Run>();
                                paragraph.Append(new Run(drawing));
                                continue;
                            }
                        }
                        // Pas d'image : la balise devient son texte de remplacement (peut être vide)
                        texts = new Dictionary<string, string>(texts) { [imageTag] = img?.FallbackText ?? "" };
                    }

                    // 3. Texte
                    ReplaceTextTags(paragraph, texts, monospaceTags);
                }
            }
        }

        private sealed class RootTarget
        {
            public OpenXmlPart Part;
            public OpenXmlPartRootElement Root;
        }

        private static IEnumerable<RootTarget> GetRoots(WordprocessingDocument doc)
        {
            var main = doc.MainDocumentPart;
            if (main == null) yield break;
            yield return new RootTarget { Part = main, Root = main.Document };
            foreach (var h in main.HeaderParts) yield return new RootTarget { Part = h, Root = h.Header };
            foreach (var f in main.FooterParts) yield return new RootTarget { Part = f, Root = f.Footer };
        }

        /// <summary>
        /// Remplace les balises d'un paragraphe. Si une balise tient dans un seul morceau de texte (cas
        /// normal), seul ce morceau change : la mise en forme du reste de la ligne est conservée
        /// ("Société :" en gras, valeur en normal). Si Word a découpé une balise sur plusieurs morceaux
        /// ("{{SOC" + "IETE}}"), le paragraphe est réécrit dans son premier morceau.
        /// </summary>
        private static void ReplaceTextTags(Paragraph paragraph, IDictionary<string, string> texts, ICollection<string> monospaceTags)
        {
            bool known(string tag) => texts.ContainsKey(tag);

            // a) Balises entières dans un seul élément texte
            foreach (var t in paragraph.Descendants<Text>().ToList())
            {
                if (t.Text == null || !t.Text.Contains("{{")) continue;
                var matches = TagRegex.Matches(t.Text).Cast<Match>().Where(m => known(m.Groups[1].Value)).ToList();
                if (matches.Count == 0) continue;
                bool mono = matches.Any(m => monospaceTags.Contains(m.Groups[1].Value));
                string replaced = TagRegex.Replace(t.Text, m => known(m.Groups[1].Value) ? (texts[m.Groups[1].Value] ?? "") : m.Value);
                SetTextWithLineBreaks(t, replaced);
                if (mono) ApplyMonospace(t.Ancestors<Run>().FirstOrDefault());
            }

            // b) Balises connues découpées sur plusieurs éléments texte
            string full = paragraph.InnerText;
            var remaining = TagRegex.Matches(full).Cast<Match>().Where(m => known(m.Groups[1].Value)).ToList();
            if (remaining.Count == 0) return;

            var textElements = paragraph.Descendants<Text>().ToList();
            if (textElements.Count == 0) return;
            bool anyMono = remaining.Any(m => monospaceTags.Contains(m.Groups[1].Value));
            string merged = TagRegex.Replace(full, m => known(m.Groups[1].Value) ? (texts[m.Groups[1].Value] ?? "") : m.Value);
            for (int i = 1; i < textElements.Count; i++) textElements[i].Text = "";
            SetTextWithLineBreaks(textElements[0], merged);
            if (anyMono) ApplyMonospace(textElements[0].Ancestors<Run>().FirstOrDefault());
        }

        /// <summary>Écrit le texte dans l'élément ; chaque \n devient un vrai saut de ligne Word.</summary>
        private static void SetTextWithLineBreaks(Text target, string value)
        {
            string[] lines = (value ?? "").Replace("\r", "").Split('\n');
            target.Text = lines[0];
            target.Space = SpaceProcessingModeValues.Preserve;
            OpenXmlElement after = target;
            for (int i = 1; i < lines.Length; i++)
            {
                var br = new Break();
                after.InsertAfterSelf(br);
                var t = new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve };
                br.InsertAfterSelf(t);
                after = t;
            }
        }

        private static void ApplyMonospace(Run run)
        {
            if (run == null) return;
            var rPr = run.RunProperties;
            if (rPr == null) { rPr = new RunProperties(); run.PrependChild(rPr); }
            rPr.RemoveAllChildren<RunFonts>();
            rPr.PrependChild(new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New", ComplexScript = "Courier New" });
            rPr.RemoveAllChildren<FontSize>();
            rPr.Append(new FontSize { Val = "16" });
        }

        /// <summary>Crée l'image dans la bonne partie du document (corps, en-tête ou pied de page), à la
        /// taille du fichier réduite pour tenir dans les limites demandées.</summary>
        private static Drawing CreateImageDrawing(WordprocessingDocument doc, OpenXmlPart hostPart, TemplateImage img)
        {
            string ext = Path.GetExtension(img.Path).ToLowerInvariant();
            ImagePartType type;
            switch (ext)
            {
                case ".png": type = ImagePartType.Png; break;
                case ".jpg":
                case ".jpeg": type = ImagePartType.Jpeg; break;
                case ".bmp": type = ImagePartType.Bmp; break;
                case ".gif": type = ImagePartType.Gif; break;
                default: return null;
            }

            ImagePart imagePart;
            if (hostPart is MainDocumentPart mp) imagePart = mp.AddImagePart(type);
            else if (hostPart is HeaderPart hp) imagePart = hp.AddImagePart(type);
            else if (hostPart is FooterPart fp) imagePart = fp.AddImagePart(type);
            else return null;

            using (var stream = new FileStream(img.Path, FileMode.Open, FileAccess.Read))
            {
                imagePart.FeedData(stream);
            }
            string relId = hostPart.GetIdOfPart(imagePart);

            int pxW, pxH;
            var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(img.Path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            pxW = decoder.Frames[0].PixelWidth;
            pxH = decoder.Frames[0].PixelHeight;
            if (pxW <= 0 || pxH <= 0) return null;

            long emuW = (long)pxW * 9525L;
            long emuH = (long)pxH * 9525L;
            long maxW = img.MaxWidthEmu > 0 ? img.MaxWidthEmu : 6300000L;
            if (emuW > maxW) { emuH = (long)(emuH * ((double)maxW / emuW)); emuW = maxW; }
            if (img.MaxHeightEmu > 0 && emuH > img.MaxHeightEmu) { emuW = (long)(emuW * ((double)img.MaxHeightEmu / emuH)); emuH = img.MaxHeightEmu; }

            uint drawingId = NextDrawingId(doc);

            return new Drawing(
                new DW.Inline(
                    new DW.Extent { Cx = emuW, Cy = emuH },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.DocProperties { Id = drawingId, Name = $"Image {drawingId}" },
                    new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                    new A.Graphic(
                        new A.GraphicData(
                            new PIC.Picture(
                                new PIC.NonVisualPictureProperties(
                                    new PIC.NonVisualDrawingProperties { Id = 0U, Name = Path.GetFileName(img.Path) },
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
        }

        /// <summary>Identifiant d'image unique dans tout le document (corps + en-têtes + pieds de page) :
        /// deux images avec le même identifiant font signaler le fichier comme endommagé par Word.</summary>
        private static uint NextDrawingId(WordprocessingDocument doc)
        {
            uint max = 0;
            foreach (var target in GetRoots(doc))
            {
                if (target.Root == null) continue;
                foreach (var dp in target.Root.Descendants<DW.DocProperties>())
                {
                    if (dp.Id != null && dp.Id.HasValue && dp.Id.Value > max) max = dp.Id.Value;
                }
            }
            return max + 1;
        }
    }
}
